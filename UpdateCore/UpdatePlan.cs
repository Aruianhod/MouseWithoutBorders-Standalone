namespace MouseWithoutBorders.Updates;

internal sealed class UpdatePlan
{
    public string InstallDirectory { get; set; }
    public string DataDirectory { get; set; }
    public int ParentPid { get; set; }
    public long ParentStartUtcTicks { get; set; }
}
