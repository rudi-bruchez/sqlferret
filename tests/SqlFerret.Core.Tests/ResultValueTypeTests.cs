// tests/SqlFerret.Core.Tests/ResultValueTypeTests.cs
using System.Collections;
using System.Numerics;
using System.Text.Json;
using SqlFerret.Cli;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Storage;
using Xunit;

/// <summary>
/// La question de fond derrière quatre des bogues de la revue : <b>quels types CLR la
/// bibliothèque DuckDB peut-elle rendre ?</b> Personne ne la possédait — <see cref="AdHocQuery"/>
/// promettait des « valeurs brutes » et déléguait, <see cref="ResultFormatter"/> décidait par une
/// liste blanche fermée. Ces tests fixent la réponse à sa source (une vraie connexion DuckDB) puis
/// vérifient que les deux chemins de rendu la couvrent.
/// </summary>
public class ResultValueTypeTests
{
    private static string NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sf-{Guid.NewGuid():N}.duckdb");
        using var db = DuckDbProject.Open(path);
        return path;
    }

    private static object? Scalar(string expr)
    {
        var path = NewDb();
        try
        {
            using var db = DuckDbProject.OpenReadOnly(path);
            return new AdHocQuery(db.Connection).Run($"SELECT {expr} AS v").Rows[0][0];
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------------------------------
    // La source : ce que rend vraiment la bibliothèque.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1::BIGINT", typeof(long))]
    [InlineData("sum(1::BIGINT)", typeof(BigInteger))]      // HUGEINT — la cause de B1 et B2
    [InlineData("avg(1::BIGINT)", typeof(double))]
    [InlineData("1.5::DECIMAL(18,4)", typeof(decimal))]
    [InlineData("DATE '2026-01-02'", typeof(DateOnly))]     // et non DateTime — la cause de B8
    [InlineData("TIME '10:11:12'", typeof(TimeOnly))]
    [InlineData("TIMESTAMP '2026-01-02 10:11:12'", typeof(DateTime))]
    [InlineData("INTERVAL 1 DAY", typeof(TimeSpan))]
    [InlineData("'x'::VARCHAR", typeof(string))]
    [InlineData("true", typeof(bool))]
    public void DuckDb_value_types_are_the_documented_ones(string expr, Type expected)
        => Assert.IsType(expected, Scalar(expr));

    /// <summary>
    /// Le BLOB est la seule valeur qui ne survit pas au lecteur : la bibliothèque le rend en flux
    /// sur la mémoire non managée du chunk courant. <see cref="ResultTable"/> étant remis lecteur
    /// fermé, le lire après coup serait une lecture après libération — d'où la matérialisation.
    /// </summary>
    [Fact]
    public void Blob_is_detached_from_the_reader_and_still_readable_afterwards()
    {
        var v = Scalar("'abc'::BLOB");                       // le lecteur est ferme depuis
        var bytes = Assert.IsType<byte[]>(v);
        Assert.Equal("abc"u8.ToArray(), bytes);
    }

    /// <summary>
    /// Un BLOB place dans un LIST ou un STRUCT est tout aussi volatil qu'un BLOB de premier
    /// niveau. Ne detacher que le premier niveau le faisait sortir en
    /// <c>"System.IO.UnmanagedMemoryStream"</c> : exit 0, valeur fausse, aucun avertissement —
    /// une degradation en nature, puisque le cas levait bruyamment auparavant.
    /// </summary>
    [Theory]
    [InlineData("['x'::BLOB]")]
    [InlineData("{'bb': 'x'::BLOB}")]
    [InlineData("MAP{1: 'x'::BLOB}")]
    [InlineData("[{'bb': ['x'::BLOB]}]")]           // imbrication profonde
    public void A_blob_nested_in_a_composite_is_detached_too(string expr)
    {
        var v = Scalar(expr);
        var flat = Flatten(v).ToList();
        var bytes = Assert.IsType<byte[]>(Assert.Single(flat));
        Assert.Equal("x"u8.ToArray(), bytes);

        static IEnumerable<object?> Flatten(object? v) => v switch
        {
            IDictionary d => d.Values.Cast<object?>().SelectMany(Flatten),
            string or byte[] => [v],
            IEnumerable e => e.Cast<object?>().SelectMany(Flatten),
            _ => [v],
        };
    }

    /// <summary>
    /// Et le rendu doit alors montrer la valeur, pas le nom de la classe .NET — dans les deux
    /// familles de format.
    /// </summary>
    [Fact]
    public void A_nested_blob_renders_as_a_value_in_json_and_in_text()
    {
        var path = NewDb();
        try
        {
            using var db = DuckDbProject.OpenReadOnly(path);
            var t = new AdHocQuery(db.Connection)
                .Run("SELECT ['y'::BLOB] AS blist, {'bb': 'z'::BLOB} AS bstruct");

            var json = ResultFormatter.Render(t, "json", "ms", raw: false);
            Assert.DoesNotContain("UnmanagedMemoryStream", json);
            using var doc = JsonDocument.Parse(json);
            var row = doc.RootElement[0];
            Assert.Equal("0x79", row.GetProperty("blist")[0].GetString());           // 'y'
            Assert.Equal("0x7A", row.GetProperty("bstruct").GetProperty("bb").GetString());

            var text = ResultFormatter.Render(t, "table", "ms", raw: false);
            Assert.DoesNotContain("UnmanagedMemoryStream", text);
            Assert.Contains("0x79", text);
            Assert.Contains("0x7A", text);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// La contrepartie de la recopie : les composites sont normalises, donc utilisables sans
    /// connaitre le type generique que la bibliotheque avait choisi.
    /// </summary>
    [Fact]
    public void Composites_come_back_normalised()
    {
        Assert.IsType<List<object?>>(Scalar("[1, 2, 3]"));
        Assert.IsType<Dictionary<string, object?>>(Scalar("{'a': 1}"));
        Assert.IsType<Dictionary<string, object?>>(Scalar("MAP{1: 'a'}"));
        Assert.IsType<string>(Scalar("'abc'"));      // une chaine reste une chaine
    }

    // -----------------------------------------------------------------------------------------
    // B1 : --format json corrompait toute valeur HUGEINT.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Json_keeps_a_hugeint_as_a_number_instead_of_reflecting_its_properties()
    {
        // Valeur relevee sur la vraie trace : SELECT sum(duration_us) FROM executions.
        var t = new ResultTable(["total_us", "max_us"],
            [[BigInteger.Parse("22288368299"), 1_684_351_854L]], Truncated: false);

        var s = ResultFormatter.Render(t, "json", "ms", raw: false);

        Assert.DoesNotContain("IsPowerOfTwo", s);            // la reflexion, avant
        using var doc = JsonDocument.Parse(s);
        var row = doc.RootElement[0];
        Assert.Equal(JsonValueKind.Number, row.GetProperty("total_us").ValueKind);
        Assert.Equal(22288368299L, row.GetProperty("total_us").GetInt64());
        Assert.Equal(1684351854L, row.GetProperty("max_us").GetInt64());
    }

    /// <summary>
    /// Un HUGEINT peut dépasser <c>long</c> : la valeur doit rester un littéral JSON exact, pas
    /// être ramenée à un type plus étroit ni passer en chaîne.
    /// </summary>
    [Fact]
    public void Json_keeps_a_hugeint_wider_than_long()
    {
        var huge = BigInteger.Pow(2, 100) + 7;
        var t = new ResultTable(["n"], [[huge]], Truncated: false);

        var s = ResultFormatter.Render(t, "json", "ms", raw: false);

        using var doc = JsonDocument.Parse(s);
        var n = doc.RootElement[0].GetProperty("n");
        Assert.Equal(JsonValueKind.Number, n.ValueKind);
        Assert.Equal(huge.ToString(), n.GetRawText());
    }

    // -----------------------------------------------------------------------------------------
    // B2 : deux colonnes _us de la meme ligne sortaient dans deux unites differentes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Every_numeric_type_in_a_us_column_gets_the_same_unit()
    {
        // Exactement la ligne de la reproduction : total_us en HUGEINT, max_us en BIGINT,
        // avg_us en DOUBLE. Avant, seul max_us etait converti.
        var t = new ResultTable(["total_us", "max_us", "avg_us", "dec_us"],
            [[BigInteger.Parse("22288368299"), 1_684_351_854L, 62289.504746813334d, 2_500.9m]],
            Truncated: false);

        var s = ResultFormatter.Render(t, "csv", "ms", raw: false);
        var values = s.Split(Environment.NewLine)[1].Split(',');

        Assert.All(values, v => Assert.EndsWith(" ms", v));
        Assert.Equal("22288368 ms", values[0]);
        Assert.Equal("1684351 ms", values[1]);
        Assert.Equal("62 ms", values[2]);
        Assert.Equal("2 ms", values[3]);
    }

    [Fact]
    public void Raw_still_bypasses_the_conversion_for_every_numeric_type()
    {
        var t = new ResultTable(["total_us", "avg_us"],
            [[BigInteger.Parse("22288368299"), 62289.5d]], Truncated: false);

        var s = ResultFormatter.Render(t, "csv", "ms", raw: true);

        Assert.Contains("22288368299", s);
        Assert.Contains("62289.5", s);
        Assert.DoesNotContain(" ms", s);
    }

    /// <summary>
    /// Une colonne <c>_us</c> qui ne porte pas un nombre n'est pas une durée : elle doit sortir
    /// telle quelle, et surtout pas être convertie de travers.
    /// </summary>
    [Fact]
    public void A_non_numeric_us_column_is_left_alone()
    {
        var t = new ResultTable(["note_us"], [["n/a"]], Truncated: false);
        Assert.Contains("n/a", ResultFormatter.Render(t, "table", "ms", raw: false));
    }

    // -----------------------------------------------------------------------------------------
    // B8 : DATE et TIME tombaient dans IFormattable — date ambigue, secondes perdues.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Date_and_time_are_unambiguous_and_keep_their_seconds()
    {
        var t = new ResultTable(["jour", "heure", "duree"],
            [[new DateOnly(2026, 1, 2), new TimeOnly(10, 11, 12), TimeSpan.FromDays(1)]],
            Truncated: false);

        var s = ResultFormatter.Render(t, "table", "ms", raw: false);

        Assert.Contains("2026-01-02", s);        // et non "01/02/2026"
        Assert.Contains("10:11:12", s);          // et non "10:11"
        Assert.DoesNotContain("01/02/2026", s);
        Assert.Contains("1.00:00:00", s);
    }

    [Fact]
    public void Json_writes_date_and_time_as_strings_not_as_reflected_objects()
    {
        var t = new ResultTable(["jour", "heure", "ts"],
            [[new DateOnly(2026, 1, 2), new TimeOnly(10, 11, 12),
              new DateTime(2026, 8, 13, 5, 31, 22, 123)]], Truncated: false);

        var s = ResultFormatter.Render(t, "json", "ms", raw: false);

        using var doc = JsonDocument.Parse(s);
        var row = doc.RootElement[0];
        Assert.Equal("2026-01-02", row.GetProperty("jour").GetString());
        Assert.StartsWith("10:11:12", row.GetProperty("heure").GetString());
        Assert.StartsWith("2026-08-13T05:31:22", row.GetProperty("ts").GetString());   // ISO 8601
    }

    // -----------------------------------------------------------------------------------------
    // Les types restants du jeu documente : rien ne doit sortir en nom de classe .NET.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Blob_guid_and_composites_render_as_values_in_both_families()
    {
        var g = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var t = new ResultTable(["b", "g", "liste", "structure"],
            [[
                "abc"u8.ToArray(), g, new List<int> { 1, 2, 3 },
                new Dictionary<string, object?> { ["a"] = 1L, ["b"] = null },
            ]], Truncated: false);

        var text = ResultFormatter.Render(t, "table", "ms", raw: false);
        Assert.Contains("0x616263", text);
        Assert.Contains(g.ToString(), text);
        Assert.Contains("[1,2,3]", text);
        Assert.DoesNotContain("System.Collections", text);   // le nom de classe, avant

        var json = ResultFormatter.Render(t, "json", "ms", raw: false);
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement[0];
        Assert.Equal("0x616263", row.GetProperty("b").GetString());
        Assert.Equal(3, row.GetProperty("liste").GetArrayLength());
        Assert.Equal(1, row.GetProperty("structure").GetProperty("a").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("structure").GetProperty("b").ValueKind);
    }

    /// <summary>
    /// <c>NaN</c> et l'infini n'existent pas en JSON. Les écrire en nombre lèverait et
    /// emporterait toute la sortie ; ils sortent donc en chaîne, ce qui n'efface rien.
    /// </summary>
    [Fact]
    public void Json_does_not_lose_the_whole_output_on_a_non_finite_double()
    {
        var t = new ResultTable(["x"], [[double.NaN]], Truncated: false);
        var s = ResultFormatter.Render(t, "json", "ms", raw: false);
        using var doc = JsonDocument.Parse(s);
        Assert.Equal("NaN", doc.RootElement[0].GetProperty("x").GetString());
    }
}
