// tests/SqlFerret.Core.Tests/CliQueryCommandTests.cs
using System.Diagnostics;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Storage;
using Xunit;

/// <summary>
/// Ce que la commande <c>query</c> fait vraiment : code de retour, sortie standard, sortie
/// d'erreur. Trois des constats de la revue ne vivent que dans l'analyse des arguments et le
/// placement du <c>try</c> de <c>Program.cs</c> — un fichier d'instructions de haut niveau, sans
/// composant à isoler. On le lance donc pour de vrai.
/// <para>Le binaire est cherché à l'emplacement documenté ; le test est ignoré proprement s'il
/// n'a pas été construit, comme les tests dépendant de <c>sample/</c>.</para>
/// </summary>
public class CliQueryCommandTests
{
    private static string? CliPath()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        foreach (var config in new[] { "Debug", "Release" })
        {
            var exe = Path.Combine(root, "src", "SqlFerret.Cli", "bin", config, "net10.0",
                OperatingSystem.IsWindows() ? "SqlFerret.Cli.exe" : "SqlFerret.Cli");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private sealed record Run(int ExitCode, string Out, string Err);

    private static Run Cli(string projectDir, params string[] args)
    {
        var psi = new ProcessStartInfo(CliPath()!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("query");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(projectDir);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new Run(p.ExitCode, stdout, stderr);
    }

    /// <summary>Projet jetable contenant <paramref name="rows"/> signatures.</summary>
    private static string NewProject(int rows)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sf_cli_{Guid.NewGuid():N}");
        var project = SqlFerret.Core.Project.AuditProject.OpenOrCreate(dir);
        using var db = project.OpenDb();
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"""
          INSERT INTO normalized_queries
                (normalized_hash, normalized_sql, statement_kind, primary_table,
                 target_object, normalizer_version, first_seen_at, last_seen_at)
          SELECT 'h' || i, 's', 'SELECT', 'dbo.T', null, {SqlFerret.Core.Normalization.QueryNormalizer.Version},
                 '2026-08-13', '2026-08-13'
          FROM range({rows}) t(i)
          """;
        c.ExecuteNonQuery();
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* verrou Windows */ }
    }

    // -----------------------------------------------------------------------------------------
    // B4 : --limit non numerique valait "aucune limite", silencieusement, avec un code retour 0.
    // -----------------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("3.5")]
    public void An_invalid_limit_is_refused_instead_of_meaning_no_limit(string value)
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(5);
        try
        {
            var r = Cli(dir, "--sql", "SELECT normalized_hash FROM normalized_queries", "--limit", value);

            Assert.Equal(1, r.ExitCode);
            Assert.Contains("--limit", r.Err);
            Assert.Empty(r.Out.Trim());
        }
        finally { Cleanup(dir); }
    }

    // -----------------------------------------------------------------------------------------
    // B5 : sans limite par defaut, un SELECT * FROM executions — commande parfaitement normale —
    // materialisait tout le jeu de resultats et finissait en OutOfMemoryException non gardee.
    // -----------------------------------------------------------------------------------------

    [SkippableFact]
    public void Without_limit_a_default_applies_and_the_truncation_is_announced()
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(AdHocQuery.DefaultLimit + 10);
        try
        {
            var r = Cli(dir, "--sql", "SELECT normalized_hash FROM normalized_queries", "--format", "csv");

            Assert.Equal(0, r.ExitCode);
            Assert.Contains("tronquee", r.Err);
            Assert.Contains(AdHocQuery.DefaultLimit.ToString(), r.Err);
            // entete + DefaultLimit lignes
            Assert.Equal(AdHocQuery.DefaultLimit + 1,
                r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// `--limit` en dernier argument : Arg() rendait "" aussi bien pour un drapeau absent que pour
    /// un drapeau sans valeur, si bien que la ligne retombait sur la limite par defaut sans un mot
    /// — le residu de la faute exacte que la validation de --limit corrigeait.
    /// </summary>
    [SkippableFact]
    public void A_limit_flag_without_a_value_is_refused_instead_of_falling_back()
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(5);
        try
        {
            var r = Cli(dir, "--sql", "SELECT normalized_hash FROM normalized_queries", "--limit");

            Assert.Equal(1, r.ExitCode);
            Assert.Contains("--limit", r.Err);
            Assert.Empty(r.Out.Trim());
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Le pied de page du tableau annoncait `(sortie tronquée par --limit)` alors que la
    /// troncature vient de la limite par defaut dans la plupart des cas. Il dit desormais la meme
    /// chose que stderr, par construction.
    /// </summary>
    [SkippableFact]
    public void The_table_footer_names_the_same_cause_as_stderr()
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(AdHocQuery.DefaultLimit + 10);
        try
        {
            var d = Cli(dir, "--sql", "SELECT normalized_hash FROM normalized_queries");
            Assert.Contains("limite par defaut", d.Out);          // pied de page
            Assert.Contains("limite par defaut", d.Err);          // stderr
            Assert.DoesNotContain("tronquée par --limit", d.Out); // l'ancienne affirmation fausse

            var e = Cli(dir, "--sql", "SELECT normalized_hash FROM normalized_queries", "--limit", "5");
            Assert.Contains("--limit", e.Out);
            Assert.DoesNotContain("limite par defaut", e.Out);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Un BLOB imbrique dans un LIST ou un STRUCT sortait en nom de classe .NET : exit 0, valeur
    /// fausse, aucun avertissement.
    /// </summary>
    [SkippableFact]
    public void A_nested_blob_is_rendered_as_a_value_end_to_end()
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(1);
        try
        {
            var r = Cli(dir, "--format", "json",
                        "--sql", "SELECT ['y'::BLOB] AS blist, {'bb':'z'::BLOB} AS bstruct");

            Assert.Equal(0, r.ExitCode);
            Assert.DoesNotContain("UnmanagedMemoryStream", r.Out);
            Assert.Contains("0x79", r.Out);
            Assert.Contains("0x7A", r.Out);
        }
        finally { Cleanup(dir); }
    }

    [SkippableFact]
    public void No_limit_lifts_the_default_explicitly()
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(AdHocQuery.DefaultLimit + 10);
        try
        {
            var r = Cli(dir, "--sql", "SELECT normalized_hash FROM normalized_queries",
                        "--format", "csv", "--no-limit");

            Assert.Equal(0, r.ExitCode);
            Assert.DoesNotContain("tronquee", r.Err);
            Assert.Equal(AdHocQuery.DefaultLimit + 11,
                r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { Cleanup(dir); }
    }

    [SkippableFact]
    public void Limit_and_no_limit_together_are_refused_rather_than_arbitrated()
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(5);
        try
        {
            var r = Cli(dir, "--sql", "SELECT 1", "--limit", "2", "--no-limit");
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("exclusifs", r.Err);
        }
        finally { Cleanup(dir); }
    }

    // -----------------------------------------------------------------------------------------
    // B6 : le rendu etait hors du try, alors que le commentaire promettait "un message, pas une
    // trace de pile". Un BLOB en json suffisait a faire remonter une InvalidOperationException.
    // -----------------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData("json")]
    [InlineData("table")]
    [InlineData("md")]
    [InlineData("csv")]
    public void Rendering_a_blob_never_produces_a_stack_trace(string format)
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(1);
        try
        {
            var r = Cli(dir, "--sql", "SELECT 'x'::BLOB AS b", "--format", format);

            Assert.DoesNotContain("Unhandled exception", r.Err);
            Assert.DoesNotContain("   at ", r.Err);
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("0x78", r.Out);              // 'x'
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Le cas de la reproduction, bout en bout : <c>sum()</c> rend un HUGEINT, et le format
    /// explicitement declare « machine » doit en conserver la valeur.
    /// </summary>
    [SkippableFact]
    public void A_sum_survives_the_json_format()
    {
        Skip.If(CliPath() is null, "SqlFerret.Cli non construit — test ignoré");
        var dir = NewProject(3);
        try
        {
            var r = Cli(dir, "--format", "json",
                        "--sql", "SELECT sum(normalizer_version)::HUGEINT AS total_us FROM normalized_queries");

            Assert.Equal(0, r.ExitCode);
            Assert.DoesNotContain("IsPowerOfTwo", r.Out);
            Assert.Contains($"\"total_us\": {3 * SqlFerret.Core.Normalization.QueryNormalizer.Version}", r.Out);
        }
        finally { Cleanup(dir); }
    }
}
