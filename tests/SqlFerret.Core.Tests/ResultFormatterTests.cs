// tests/SqlFerret.Core.Tests/ResultFormatterTests.cs
using SqlFerret.Cli;
using SqlFerret.Core.Analysis;
using Xunit;

public class ResultFormatterTests
{
    private static ResultTable Table() => new(
        ["kind", "total_us"],
        [
            ["CREATE INDEX", 1_500_000L],
            ["SELECT", 2_000L],
        ],
        Truncated: false);

    [Fact]
    public void Table_format_converts_us_columns()
    {
        var s = ResultFormatter.Render(Table(), "table", "ms", raw: false);
        Assert.Contains("1500 ms", s);
        Assert.Contains("2 ms", s);
    }

    [Fact]
    public void Raw_keeps_microseconds()
    {
        var s = ResultFormatter.Render(Table(), "table", "ms", raw: true);
        Assert.Contains("1500000", s);
        Assert.DoesNotContain("1500 ms", s);
    }

    [Fact]
    public void Column_without_us_suffix_is_not_formatted()
    {
        var t = new ResultTable(["duration"], [[1_500_000L]], Truncated: false);
        var s = ResultFormatter.Render(t, "table", "ms", raw: false);
        Assert.Contains("1500000", s);
    }

    [Fact]
    public void Markdown_escapes_pipes_and_notes_truncation()
    {
        var t = new ResultTable(["sql"], [["a | b"]], Truncated: true);
        var s = ResultFormatter.Render(t, "md", "ms", raw: false);
        Assert.Contains(@"a \| b", s);
        Assert.Contains("tronqu", s);          // la note de troncature est presente
    }

    [Fact]
    public void Csv_quotes_separators_and_quotes()
    {
        var t = new ResultTable(["a", "b"], [["x,y", "he said \"hi\""]], Truncated: false);
        var s = ResultFormatter.Render(t, "csv", "ms", raw: false);
        Assert.Contains("\"x,y\"", s);
        Assert.Contains("\"he said \"\"hi\"\"\"", s);
    }

    [Fact]
    public void Json_emits_array_of_objects_with_nulls()
    {
        var t = new ResultTable(["a", "b"], [["x", null]], Truncated: false);
        var s = ResultFormatter.Render(t, "json", "ms", raw: false);
        Assert.Contains("\"a\":", s);
        Assert.Contains("null", s);
    }

    [Fact]
    public void Json_keeps_numbers_typed_and_ignores_us_formatting()
    {
        var s = ResultFormatter.Render(Table(), "json", "ms", raw: false);
        Assert.Contains("1500000", s);        // valeur brute, pas "1500 ms"
        Assert.DoesNotContain("\"1500000\"", s);   // et non quotee : un nombre reste un nombre
    }

    [Fact]
    public void Timestamps_are_iso_sortable()
    {
        var t = new ResultTable(["captured_at"],
            [[new DateTime(2026, 8, 13, 5, 31, 22, 123)]], Truncated: false);
        var s = ResultFormatter.Render(t, "table", "ms", raw: false);
        Assert.Contains("2026-08-13 05:31:22.123", s);
    }

    [Fact]
    public void Duplicate_json_columns_are_flagged_so_the_silent_overwrite_is_signaled()
    {
        // JOIN sans alias : "id" apparait des deux cotes. Render("json") produit quand meme un
        // objet valide (une seule cle "id", la seconde valeur ecrase la premiere) — ce test verifie
        // que l'appelant dispose d'un moyen de le detecter, pas seulement que le JSON se genere.
        var t = new ResultTable(["id", "name", "id"], [["1", "a", "2"]], Truncated: false);

        var s = ResultFormatter.Render(t, "json", "ms", raw: false);
        Assert.Contains("\"id\":", s);   // le JSON reste valide : ecrasement, pas de plantage

        var dupes = ResultFormatter.DuplicateColumnNames(t);
        Assert.Equal(["id"], dupes);
    }

    [Fact]
    public void Non_json_formats_are_unaffected_by_duplicate_column_detection()
    {
        // table/csv/md adressent par position, pas par nom : la detection existe pour "json" mais
        // ne doit rien casser ni rien signaler pour les autres formats (elle est une propriete de
        // la table de resultat ; c'est l'appelant qui decide de n'avertir qu'en json).
        var t = new ResultTable(["id", "id"], [["1", "2"]], Truncated: false);
        var s = ResultFormatter.Render(t, "table", "ms", raw: false);
        Assert.Contains("1", s);
        Assert.Contains("2", s);
    }
    // -----------------------------------------------------------------------------------------
    // B7 : table et md cassaient des qu'une valeur contenait un saut de ligne. Or
    // executions.sql_text_raw en contient systematiquement, et le skill recommande --format md.
    // -----------------------------------------------------------------------------------------

    private const string MultiLine = "SELECT 1\r\nFROM dbo.T\n\tWHERE x = 1";

    [Fact]
    public void Markdown_keeps_one_record_per_line_when_a_value_contains_newlines()
    {
        var t = new ResultTable(["id", "sql"], [["1", MultiLine], ["2", "SELECT 2"]], Truncated: false);

        var s = ResultFormatter.Render(t, "md", "ms", raw: false);
        var lines = s.Split(Environment.NewLine, StringSplitOptions.None);

        Assert.Equal(4, lines.Length);                      // entete + separateur + 2 lignes
        Assert.All(lines, l => Assert.StartsWith("|", l));
        Assert.All(lines, l => Assert.EndsWith("|", l));
        Assert.Contains(@"SELECT 1\r\nFROM dbo.T\n\tWHERE x = 1", s);
        Assert.Contains("SELECT 2", s);
    }

    [Fact]
    public void Aligned_table_keeps_its_grid_when_a_value_contains_newlines()
    {
        var t = new ResultTable(["id", "sql"], [["1", MultiLine], ["2", "SELECT 2"]], Truncated: false);

        var s = ResultFormatter.Render(t, "table", "ms", raw: false);
        var lines = s.Split(Environment.NewLine, StringSplitOptions.None);

        Assert.Equal(4, lines.Length);                      // entete + tirets + 2 lignes
        Assert.DoesNotContain("\n", s.Replace(Environment.NewLine, ""));
    }

    /// <summary>
    /// csv est un format fidele : il cite la valeur (RFC 4180) et conserve le saut de ligne tel
    /// quel. C'est la contrepartie assumee de l'echappement pratique par table et md.
    /// </summary>
    [Fact]
    public void Csv_still_preserves_newlines_verbatim()
    {
        var t = new ResultTable(["sql"], [[MultiLine]], Truncated: false);
        var s = ResultFormatter.Render(t, "csv", "ms", raw: false);
        Assert.Contains("\"SELECT 1\r\nFROM dbo.T\n\tWHERE x = 1\"", s);
    }

    /// <summary>
    /// L'antislash n'est pas double : DOMAIN\host et les chemins Windows sont partout dans ces
    /// traces, et les rendre illisibles couterait plus que n'en rapporterait une reversibilite que
    /// ces deux formats de presentation ne promettent pas.
    /// </summary>
    [Fact]
    public void A_backslash_in_a_value_is_left_readable()
    {
        var t = new ResultTable(["host"], [[@"DOMAIN\srv01"]], Truncated: false);
        Assert.Contains(@"DOMAIN\srv01", ResultFormatter.Render(t, "md", "ms", raw: false));
    }
}
