using System;
using System.Runtime.InteropServices;

namespace AxmolHub;

/// <summary>Delivers a Windows toast in process, through the WinRT ABI directly.
///
/// <para>Why not the projection: <c>Windows.UI.Notifications</c> is only projected in a
/// <c>net8.0-windows10.0.x</c> build, and adopting that TFM silently pruned
/// <c>HarfBuzzSharp.NativeAssets.macOS</c> from the <c>osx</c> publish (see the csproj note). Why not a
/// <c>powershell.exe</c> child: this machine's Windows PowerShell 5.1 cannot resolve
/// <c>ContentType=WindowsRuntime</c> types at all — measured 2026-10-10, `[Windows.Foundation.Uri,
/// Windows.Foundation, ContentType=WindowsRuntime]` fails just as `ToastNotificationManager` does, while the
/// WinMetadata folder and the GAC assembly are both present. So the child exits 1 on every toast and the
/// notification never exists.</para>
///
/// <para>Everything here is ABI: no NuGet dependency, no TFM change, nothing that can be pruned by the
/// platform's asset graph. The IIDs and the vtable slots were extracted and exercised by the throwaway probe
/// in <c>tmp/winrt-probe</c> (it printed <c>RESULT=OK</c> and put a toast on screen) rather than recalled, and
/// <c>--verify-shell</c> pins the literals so a mistyped one cannot survive the way a wrong
/// <c>IID_ITaskbarList3</c> once did.</para></summary>
internal static class WindowsToastInterop
{
    // 各 ABI 接口的 IID。来源是 tmp/winrt-probe 反射自投影程序集的实测值（CsWinRT 的 Type.GUID 就是
    // WinRT 的 IID），不是记忆值 —— 记忆里的 IToastNotificationManagerStatics 是错的。
    internal static readonly Guid XmlDocumentInterface = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    internal static readonly Guid XmlDocumentIoInterface = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
    internal static readonly Guid ToastNotificationFactoryInterface = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    internal static readonly Guid ToastNotificationManagerStaticsInterface = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    internal static readonly Guid ToastNotifierInterface = new("75927b93-03f3-41ec-91d3-6e5bac1b38e7");

    /// <summary>WinRT 接口的前 6 槽是 IUnknown（0 QueryInterface / 1 AddRef / 2 Release）与 IInspectable
    /// （3 GetIids / 4 GetRuntimeClassName / 5 GetTrustLevel），业务方法从槽 6 起，顺序即 IDL 声明顺序。
    /// 槽号具名而不是写裸数字：badge 那边一个写错的字面量曾让红点在整个会话里静默失效。</summary>
    private const int QueryInterfaceSlot = 0;
    private const int LoadXmlSlot = 6;
    private const int CreateToastNotificationSlot = 6;
    private const int CreateToastNotifierSlot = 7;
    private const int ShowSlot = 6;

    private const string XmlDocumentClass = "Windows.Data.Xml.Dom.XmlDocument";
    private const string ToastNotificationClass = "Windows.UI.Notifications.ToastNotification";
    private const string ToastNotificationManagerClass = "Windows.UI.Notifications.ToastNotificationManager";

    private const uint RoInitMultithreaded = 1;
    private const int SFalse = 1;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    /// <summary>Every failure names the step that refused and the HRESULT it answered with: "no notification
    /// appeared" and "a notification was never asked for" have to stay distinguishable in the log, which is
    /// the distinction the whole 0.8.x notification investigation turned on.</summary>
    internal static bool TryShow(string appUserModelId, string toastXml, out string step, out int hr)
    {
        using var apartment = RoApartment.Enter();
        HString? xmlClass = null, toastClass = null, managerClass = null, xml = null, appId = null;
        var xmlInspectable = IntPtr.Zero;
        var xmlIo = IntPtr.Zero;
        var xmlDocument = IntPtr.Zero;
        var toastFactory = IntPtr.Zero;
        var notification = IntPtr.Zero;
        var managerStatics = IntPtr.Zero;
        var notifier = IntPtr.Zero;
        try
        {
            step = "allocating the WinRT class names";
            xmlClass = HString.Create(XmlDocumentClass);
            toastClass = HString.Create(ToastNotificationClass);
            managerClass = HString.Create(ToastNotificationManagerClass);

            // RoActivateInstance 交回的是 IInspectable*，不是默认接口：LoadXml 要 IXmlDocumentIO、
            // 工厂要 IXmlDocument，两个都得自己 QI 出来，否则是拿 IInspectable 的 vtable 越界调用。
            step = "RoActivateInstance(XmlDocument)";
            hr = RoActivateInstance(xmlClass.Value, out xmlInspectable);
            if (hr != 0) return false;

            step = "QueryInterface(IXmlDocumentIO)";
            hr = QueryInterface(xmlInspectable, XmlDocumentIoInterface, out xmlIo);
            if (hr != 0) return false;

            step = "IXmlDocumentIO::LoadXml";
            xml = HString.Create(toastXml);
            hr = InvokeLoadXml(xmlIo, xml.Value);
            if (hr != 0) return false;

            step = "QueryInterface(IXmlDocument)";
            hr = QueryInterface(xmlInspectable, XmlDocumentInterface, out xmlDocument);
            if (hr != 0) return false;

            step = "RoGetActivationFactory(ToastNotification)";
            var factoryId = ToastNotificationFactoryInterface;
            hr = RoGetActivationFactory(toastClass.Value, ref factoryId, out toastFactory);
            if (hr != 0) return false;

            step = "IToastNotificationFactory::CreateToastNotification";
            hr = InvokeCreateToastNotification(toastFactory, xmlDocument, out notification);
            if (hr != 0) return false;

            step = "RoGetActivationFactory(ToastNotificationManager)";
            var staticsId = ToastNotificationManagerStaticsInterface;
            hr = RoGetActivationFactory(managerClass.Value, ref staticsId, out managerStatics);
            if (hr != 0) return false;

            step = "IToastNotificationManagerStatics::CreateToastNotifier";
            appId = HString.Create(appUserModelId);
            hr = InvokeCreateToastNotifier(managerStatics, appId.Value, out notifier);
            if (hr != 0) return false;

            step = "IToastNotifier::Show";
            hr = InvokeShow(notifier, notification);
            return hr == 0;
        }
        finally
        {
            Release(notifier);
            Release(managerStatics);
            Release(notification);
            Release(toastFactory);
            Release(xmlDocument);
            Release(xmlIo);
            Release(xmlInspectable);
            appId?.Dispose();
            xml?.Dispose();
            managerClass?.Dispose();
            toastClass?.Dispose();
            xmlClass?.Dispose();
        }
    }

    /// <summary>The silent half of the pair: builds the same objects and every notifier up to the point where
    /// <see cref="ShowSlot"/> would paint something, so <c>--verify-shell</c> can prove on the machine it runs
    /// on that these literals activate real objects. The Show slot is only read, never called.</summary>
    internal static bool TryProbe(out string step, out int hr)
    {
        using var apartment = RoApartment.Enter();
        HString? xmlClass = null, toastClass = null, managerClass = null, xml = null, appId = null;
        var xmlInspectable = IntPtr.Zero;
        var xmlIo = IntPtr.Zero;
        var xmlDocument = IntPtr.Zero;
        var toastFactory = IntPtr.Zero;
        var notification = IntPtr.Zero;
        var managerStatics = IntPtr.Zero;
        var notifier = IntPtr.Zero;
        try
        {
            step = "allocating the WinRT class names";
            xmlClass = HString.Create(XmlDocumentClass);
            toastClass = HString.Create(ToastNotificationClass);
            managerClass = HString.Create(ToastNotificationManagerClass);

            step = "RoActivateInstance(XmlDocument)";
            hr = RoActivateInstance(xmlClass.Value, out xmlInspectable);
            if (hr != 0) return false;

            step = "QueryInterface(IXmlDocumentIO)";
            hr = QueryInterface(xmlInspectable, XmlDocumentIoInterface, out xmlIo);
            if (hr != 0) return false;

            step = "IXmlDocumentIO::LoadXml";
            xml = HString.Create("<toast><visual><binding template=\"ToastGeneric\"><text>probe</text>"
                                 + "</binding></visual></toast>");
            hr = InvokeLoadXml(xmlIo, xml.Value);
            if (hr != 0) return false;

            step = "QueryInterface(IXmlDocument)";
            hr = QueryInterface(xmlInspectable, XmlDocumentInterface, out xmlDocument);
            if (hr != 0) return false;

            step = "RoGetActivationFactory(ToastNotification)";
            var factoryId = ToastNotificationFactoryInterface;
            hr = RoGetActivationFactory(toastClass.Value, ref factoryId, out toastFactory);
            if (hr != 0) return false;

            step = "IToastNotificationFactory::CreateToastNotification";
            hr = InvokeCreateToastNotification(toastFactory, xmlDocument, out notification);
            if (hr != 0) return false;

            step = "RoGetActivationFactory(ToastNotificationManager)";
            var staticsId = ToastNotificationManagerStaticsInterface;
            hr = RoGetActivationFactory(managerClass.Value, ref staticsId, out managerStatics);
            if (hr != 0) return false;

            step = "IToastNotificationManagerStatics::CreateToastNotifier";
            appId = HString.Create(SystemAttentionService.WindowsAppUserModelId);
            hr = InvokeCreateToastNotifier(managerStatics, appId.Value, out notifier);
            if (hr != 0) return false;

            step = "IToastNotifier::Show slot is readable";
            if (Slot(notifier, ShowSlot) == IntPtr.Zero) return false;
            return true;
        }
        finally
        {
            Release(notifier);
            Release(managerStatics);
            Release(notification);
            Release(toastFactory);
            Release(xmlDocument);
            Release(xmlIo);
            Release(xmlInspectable);
            appId?.Dispose();
            xml?.Dispose();
            managerClass?.Dispose();
            toastClass?.Dispose();
            xmlClass?.Dispose();
        }
    }

    internal static string Code(int hr) => $"0x{hr:x8}";

    // ---------------- ABI plumbing ----------------

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero) Marshal.Release(instance);
    }

    /// <summary>槽 0，每个接口都有。</summary>
    private static int QueryInterface(IntPtr instance, Guid interfaceId, out IntPtr target)
    {
        var id = interfaceId;
        return Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(Slot(instance, QueryInterfaceSlot))
            .Invoke(instance, ref id, out target);
    }

    private static int InvokeLoadXml(IntPtr xmlIo, IntPtr text)
        => Marshal.GetDelegateForFunctionPointer<LoadXmlDelegate>(Slot(xmlIo, LoadXmlSlot)).Invoke(xmlIo, text);

    private static int InvokeCreateToastNotification(IntPtr factory, IntPtr xmlDocument, out IntPtr notification)
        => Marshal.GetDelegateForFunctionPointer<CreateToastNotificationDelegate>(
                Slot(factory, CreateToastNotificationSlot))
            .Invoke(factory, xmlDocument, out notification);

    private static int InvokeCreateToastNotifier(IntPtr statics, IntPtr appId, out IntPtr notifier)
        => Marshal.GetDelegateForFunctionPointer<CreateToastNotifierDelegate>(
                Slot(statics, CreateToastNotifierSlot))
            .Invoke(statics, appId, out notifier);

    private static int InvokeShow(IntPtr notifier, IntPtr notification)
        => Marshal.GetDelegateForFunctionPointer<ShowDelegate>(Slot(notifier, ShowSlot))
            .Invoke(notifier, notification);

    private static IntPtr Slot(IntPtr instance, int slot)
        => Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);

    /// <summary>进入 WinRT 单元，并只在自己真的初始化了它时才退出。托管线程多半已经被 CLR 初始化过
    /// （实测 <c>RoInitialize(MTA)</c> 返回 <c>S_FALSE</c>），那时调用 <c>RoUninitialize</c> 会把别人的单元拆掉；
    /// UI 线程是 STA，则得到 <c>RPC_E_CHANGED_MODE</c> —— 两种都按"已初始化、继续用"处理。</summary>
    private readonly struct RoApartment : IDisposable
    {
        private readonly bool _owned;

        private RoApartment(bool owned) => _owned = owned;

        internal static RoApartment Enter()
        {
            var hr = RoInitialize(RoInitMultithreaded);
            return new RoApartment(hr == S_OK);
        }

        public void Dispose()
        {
            if (_owned) RoUninitialize();
        }

        private const int S_OK = 0;
    }

    private sealed class HString : IDisposable
    {
        internal IntPtr Value { get; }

        private HString(IntPtr value) => Value = value;

        internal static HString Create(string text)
        {
            var hr = WindowsCreateString(text, text.Length, out var value);
            if (hr != 0) throw new InvalidOperationException($"WindowsCreateString failed ({Code(hr)}).");
            return new HString(value);
        }

        public void Dispose()
        {
            if (Value != IntPtr.Zero) WindowsDeleteString(Value);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid interfaceId, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoadXmlDelegate(IntPtr self, IntPtr xml);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateToastNotificationDelegate(IntPtr self, IntPtr xmlDocument, out IntPtr notification);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateToastNotifierDelegate(IntPtr self, IntPtr appUserModelId, out IntPtr notifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShowDelegate(IntPtr self, IntPtr notification);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(uint initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid interfaceId, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
