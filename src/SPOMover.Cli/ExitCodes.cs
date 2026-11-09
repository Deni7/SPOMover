namespace SPOMover.Cli;

internal static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Finished, but some files need attention.</summary>
    public const int Attention = 1;
    /// <summary>Invalid profile or arguments; nothing was done.</summary>
    public const int InvalidInput = 2;
}
