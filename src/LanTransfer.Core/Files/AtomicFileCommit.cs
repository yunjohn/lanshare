namespace LanTransfer.Core.Files;

/// <summary>将同目录中的完整临时文件提交为正式文件，失败时保留已有目标。</summary>
public static class AtomicFileCommit
{
    public static void Commit(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(temporaryPath, destinationPath, null);
        else
            File.Move(temporaryPath, destinationPath);
    }
}
