namespace AxmolHub.Core;
public static partial class FileOperation
{
    internal static Func<string, CancellationToken, Task<FileOperationResult>>? RecycleHandlerForCheck { get; set; }
}
