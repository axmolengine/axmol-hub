using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AxmolHub;

/// <summary>Small UIAutomationClient ABI surface; no Windows TFM or platform package in the portable host.
/// Slots and signatures follow Microsoft's UIAutomationClient.h. All COM objects stay on one MTA thread.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAutomation
{
    private static readonly BlockingCollection<Action> Queue = new(16);
    private static readonly Thread Worker = Start();
    private static Thread Start()
    {
        var thread = new Thread(() =>
        {
            var hr = CoInitializeEx(0, 0);
            try { foreach (var call in Queue.GetConsumingEnumerable()) call(); }
            finally { if (hr >= 0) CoUninitialize(); }
        }) { IsBackground = true, Name = "Computer Use UI Automation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread;
    }

    internal static T Run<T>(Func<Client, T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Queue.TryAdd(() =>
        {
            try { using var client = new Client(); result.TrySetResult(action(client)); }
            catch (Exception ex) { result.TrySetException(ex); }
        })) throw new InvalidOperationException("UI Automation is busy.");
        // UIA has bounded connection/transaction timeouts. Do not abandon queued mutating work on timeout.
        return result.Task.GetAwaiter().GetResult();
    }

    internal sealed class Client : IDisposable
    {
        private readonly Com automation;
        private readonly Com walker;
        internal Client()
        {
            var clsid = new Guid("e22ad333-b25f-460c-83d0-0581107395c9"); // CUIAutomation8
            var iid = new Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"); // IUIAutomation2
            Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out var value));
            automation = new Com(value);
            try
            {
                Check(automation.Call<PutInt>(61)(value, 500)); // ConnectionTimeout
                Check(automation.Call<PutInt>(63)(value, 500)); // TransactionTimeout
                Check(automation.Call<PutInt>(59)(value, 0)); // AutoSetFocus: no implicit focus changes
                walker = automation.Object(16); // RawViewWalker
            }
            catch { automation.Dispose(); throw; }
        }
        internal Com FromHandle(nint window)
        { Check(automation.Call<FromHandle>(6)(automation.Value, window, out var element)); return new(element); }
        internal Com FromPoint(int x, int y)
        { Check(automation.Call<FromPoint>(7)(automation.Value, new Point { X = x, Y = y }, out var element)); return new(element); }
        internal Com Focused() => automation.Object(8);
        internal Com TextDescendants(Com root)
        {
            Check(automation.Call<Condition>(23)(automation.Value, 30003, 50004, out var edit));
            using var editCondition = new Com(edit);
            Check(automation.Call<Condition>(23)(automation.Value, 30003, 50030, out var document));
            using var documentCondition = new Com(document);
            Check(automation.Call<Combine>(28)(automation.Value, edit, document, out var either));
            using var condition = new Com(either);
            Check(root.Call<Find>(6)(root.Value, 4, either, out var result));
            return new(result);
        }
        internal Com Parent(Com element)
        { Check(walker.Call<ObjectArgument>(3)(walker.Value, element.Value, out var parent)); return new(parent); }
        internal bool Same(Com a, Com b)
        { Check(automation.Call<Compare>(3)(automation.Value, a.Value, b.Value, out var same)); return same != 0; }
        public void Dispose() { walker.Dispose(); automation.Dispose(); }
    }

    internal sealed class Com(nint value) : IDisposable
    {
        internal nint Value { get; private set; } = value != 0 ? value : throw new InvalidOperationException("UIA returned no object.");
        internal T Call<T>(int slot) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Value), slot * IntPtr.Size));
        internal Com Object(int slot)
        { Check(Call<GetObject>(slot)(Value, out var result)); return new(result); }
        internal object Property(int id, bool required = true)
        {
            Check(Call<PropertyValue>(11)(Value, id, required ? 1 : 0, out var result));
            if (Marshal.IsComObject(result))
            {
                Marshal.ReleaseComObject(result);
                throw new InvalidOperationException($"UIA property {id} is unsupported.");
            }
            return result;
        }
        internal int Int(int id) => Property(id) is int i ? i : throw new InvalidOperationException("UIA integer unavailable.");
        internal bool Bool(int id, bool required = true) => Property(id, required) is bool b ? b : throw new InvalidOperationException("UIA boolean unavailable.");
        internal string Text(int id) => Property(id, required: false) is string s && s.Length <= 4096 ? s
            : throw new InvalidOperationException("UIA metadata unavailable or too large.");
        internal Com Pattern(int id)
        {
            var iid = id switch
            {
                10014 => new Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"),
                10004 => new Guid("88f4d42a-e881-459d-a77c-73bbbb7e02dc"),
                _ => throw new NotSupportedException("Unsupported UIA pattern."),
            };
            Check(Call<PatternAs>(14)(Value, id, ref iid, out var result)); return new(result);
        }
        internal int[] RuntimeId()
        {
            Check(Call<IntArray>(4)(Value, out var id));
            return id is { Length: > 0 and <= 64 } ? id : throw new InvalidOperationException("UIA runtime identity unavailable.");
        }
        internal Rect Bounds()
        { Check(Call<GetRect>(43)(Value, out var rect)); return rect; }
        internal int Number(int slot)
        { Check(Call<GetInt>(slot)(Value, out var number)); return number; }
        internal double Real(int slot)
        { Check(Call<GetDouble>(slot)(Value, out var number)); return number; }
        internal string RangeText(int maximum)
        {
            Check(Call<GetText>(12)(Value, maximum, out var text));
            if (text.Length >= maximum) throw new InvalidOperationException("UIA text exceeds the verification bound.");
            return text;
        }
        internal Com RangeAt(int x, int y)
        { Check(Call<FromPoint>(3)(Value, new Point { X = x, Y = y }, out var range)); return new(range); }
        internal Com Item(int index)
        { Check(Call<PatternValue>(4)(Value, index, out var item)); return new(item); }
        internal void Endpoint(int endpoint, Com other, int otherEndpoint)
            => Check(Call<MoveEndpoint>(15)(Value, endpoint, other.Value, otherEndpoint));
        internal int CompareEndpoint(int endpoint, Com other, int otherEndpoint)
        { Check(Call<CompareEndpoint>(5)(Value, endpoint, other.Value, otherEndpoint, out var result)); return result; }
        internal void Select() => Check(Call<NoArgs>(16)(Value));
        internal void Focus() => Check(Call<NoArgs>(3)(Value));
        internal void Scroll(int horizontal, int vertical) => Check(Call<Scroll>(3)(Value, horizontal, vertical));
        public void Dispose()
        { var previous = Value; Value = 0; if (previous != 0) Marshal.Release(previous); }
    }

    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal record struct Rect(int Left, int Top, int Right, int Bottom);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(nint self, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ObjectArgument(nint self, nint argument, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FromHandle(nint self, nint window, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FromPoint(nint self, Point point, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Compare(nint self, nint a, nint b, out int same);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PutInt(nint self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Condition(nint self, int property,
        [MarshalAs(UnmanagedType.Struct)] object value, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Combine(nint self, nint a, nint b, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Find(nint self, int scope, nint condition, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PropertyValue(nint self, int id, int ignoreDefault,
        [MarshalAs(UnmanagedType.Struct)] out object value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PatternValue(nint self, int id, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PatternAs(nint self, int id, ref Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntArray(nint self,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetRect(nint self, out Rect value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt(nint self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDouble(nint self, out double value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetText(nint self, int maximum,
        [MarshalAs(UnmanagedType.BStr)] out string text);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MoveEndpoint(nint self, int endpoint, nint range, int otherEndpoint);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CompareEndpoint(nint self, int endpoint, nint range, int otherEndpoint, out int result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Scroll(nint self, int horizontal, int vertical);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint result);
}
