namespace WindowsDriverCore.Messages;

public record BuildInfo(string Version, string Revision, string Time);

public record OsInfo(string Arch, string Name, string Version);

public record StatusInfo(BuildInfo Build, OsInfo Os);
