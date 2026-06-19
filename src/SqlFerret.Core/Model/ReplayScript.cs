namespace SqlFerret.Core.Model;

public record ReplayScript(string Sql, ReplayKind Kind, double Confidence);
