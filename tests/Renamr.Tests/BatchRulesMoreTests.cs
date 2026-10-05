using System.Text.Json;
using System.Text.Json.Serialization;
using Renamr.Core.BatchRename;
using Renamr.Presentation.ViewModels;

namespace Renamr.Tests;

/// <summary>Le regole aggiunte nella v1.15 (spunto: i metodi di Advanced Renamer) e le migliorie a quelle di prima.</summary>
public class BatchRulesMoreTests
{
    private static readonly DateTime Noon = new(2024, 7, 14, 10, 30, 5, DateTimeKind.Utc);

    private static BatchFile F(string name) => new(Path.Combine(Path.GetTempPath(), "lib", name), 1, Noon, Noon);

    private static string N(string name, params BatchRule[] rules) => BatchRenameEngine.Apply([F(name)], rules)[0].NewName;

    private static string[] Names(string[] names, params BatchRule[] rules) =>
        [.. BatchRenameEngine.Apply([.. names.Select(F)], rules).Select(r => r.NewName)];

    // ---- Sposta testo

    [Fact]
    public void Move_text_to_the_end_or_start()
    {
        Assert.Equal("Mare Sardegna 2024.jpg", N("2024 Mare Sardegna.jpg", new MoveTextRule { Find = "2024" }));
        Assert.Equal("(2023) Foto Roma.jpg", N("Foto (2023) Roma.jpg", new MoveTextRule { Find = @"\(\d{4}\)", UseRegex = true, Target = MoveTarget.Start }));
        Assert.Equal("Nessuno qui.jpg", N("Nessuno qui.jpg", new MoveTextRule { Find = "2024" }));
    }

    [Fact]
    public void Move_characters_by_position()
    {
        var cool = new MoveTextRule { Source = MoveSource.Characters, From = 4, Count = 4 };
        Assert.Equal("cool my file name.txt", N("my cool file name.txt", cool with { Target = MoveTarget.Start }));
        Assert.Equal("my file cool name.txt", N("my cool file name.txt", cool with { Target = MoveTarget.AfterCharacters, TargetIndex = 8 }));
        Assert.Equal("my file name_cool.txt", N("my cool file name.txt", cool with { Separator = "_" }));
    }

    [Fact]
    public void Move_with_a_bad_regex_is_a_row_error()
    {
        var rule = new MoveTextRule { Find = "([", UseRegex = true };
        Assert.False(BatchRenameEngine.Apply([F("a.txt")], [rule])[0].IsValid);
        Assert.NotNull(rule.Validate());
    }

    // ---- Scambia parti

    [Fact]
    public void Swap_parts_around_a_separator()
    {
        Assert.Equal("Thriller - Michael Jackson.mp3", N("Michael Jackson - Thriller.mp3", new SwapPartsRule()));
        Assert.Equal("C - A - B.mp3", N("A - B - C.mp3", new SwapPartsRule { Occurrence = 2 }));
        Assert.Equal("C - A - B.mp3", N("A - B - C.mp3", new SwapPartsRule { FromEnd = true }));
        Assert.Equal("B, A.txt", N("A, B.txt", new SwapPartsRule { Separator = ", " }));
        Assert.Equal("Senza separatore.txt", N("Senza separatore.txt", new SwapPartsRule()));
    }

    // ---- Rinumera

    [Fact]
    public void Renumber_adds_a_value_and_keeps_the_digits()
    {
        Assert.Equal("Episodio 17 parte 2.mkv", N("Episodio 07 parte 2.mkv", new RenumberRule { Mode = RenumberMode.Add, Add = 10 }));
        Assert.Equal("Episodio 07 parte 3.mkv", N("Episodio 07 parte 2.mkv", new RenumberRule { Mode = RenumberMode.Add, FromEnd = true }));
        Assert.Equal("Episodio 007 parte 2.mkv", N("Episodio 07 parte 2.mkv", new RenumberRule { Mode = RenumberMode.Add, Add = 0, Digits = 3 }));
        Assert.Equal("Ep 3.mkv", N("Ep 3.mkv", new RenumberRule { Which = 2 }));
    }

    [Fact]
    public void Renumber_as_a_new_sequence_in_file_order()
    {
        var names = Names(["Scan 05 fronte.png", "Scan 09 fronte.png", "Scan 12 fronte.png"], new RenumberRule { Start = 1, Step = 1 });
        Assert.Equal(["Scan 01 fronte.png", "Scan 02 fronte.png", "Scan 03 fronte.png"], names);
    }

    // ---- Rifila

    [Fact]
    public void Trim_removes_the_chosen_characters_at_the_edges()
    {
        Assert.Equal("Foto.jpg", N("__-Foto-__.jpg", new TrimRule()));
        Assert.Equal("Foto--.jpg", N("--Foto--.jpg", new TrimRule { Where = TrimWhere.Start }));
        Assert.Equal("--Foto.jpg", N("--Foto--.jpg", new TrimRule { Where = TrimWhere.End }));
        Assert.Equal("Foto.jpg", N("XxFotox.jpg", new TrimRule { Characters = "x" }));
    }

    // ---- Nomi da elenco

    [Fact]
    public void Name_list_gives_one_line_per_file()
    {
        var names = Names(["a.txt", "b.txt", "c.txt", "d.txt"], new NameListRule { Names = "Uno\r\n\r\nTre {n}" });
        Assert.Equal(["Uno.txt", "b.txt", "Tre 3.txt", "d.txt"], names);
        Assert.Equal("a\nb", NameListRule.FromNames(["a", "b"]));
    }

    // ---- Sostituzioni multiple

    [Fact]
    public void Replace_list_applies_every_pair_in_order()
    {
        var rule = new ReplaceListRule { Pairs = [new("è", "e"), new("&", "e"), new("latte", "Latte")] };
        Assert.Equal("Caffe e Latte.txt", N("Caffè & latte.txt", rule));
        Assert.Equal("Caffe e latte.txt", N("Caffè & latte.txt", rule with { MatchCase = true, Pairs = [new("è", "e"), new("&", "e"), new("LATTE", "x")] }));
        Assert.Equal("2024-07-14.txt", N("14.07.2024.txt", new ReplaceListRule { UseRegex = true, Pairs = [new(@"(\d+)\.(\d+)\.(\d{4})", "$3-$2-$1")] }));

        var bad = new ReplaceListRule { UseRegex = true, Pairs = [new("ok", "x"), new("([", "y")] };
        Assert.NotNull(bad.Validate());
        Assert.False(BatchRenameEngine.Apply([F("a.txt")], [bad])[0].IsValid);
    }

    // ---- Migliorie alle regole di prima

    [Fact]
    public void Replace_only_the_first_or_last_time()
    {
        Assert.Equal("a_b-c.txt", N("a-b-c.txt", new ReplaceTextRule { Find = "-", Replacement = "_", Occurrence = ReplaceOccurrence.First }));
        Assert.Equal("a-b_c.txt", N("a-b-c.txt", new ReplaceTextRule { Find = "-", Replacement = "_", Occurrence = ReplaceOccurrence.Last }));
        Assert.Equal("IMG 2023 [2024].txt", N("IMG 2023 2024.txt", new ReplaceTextRule { Find = @"\d+", Replacement = "[$0]", UseRegex = true, Occurrence = ReplaceOccurrence.Last }));
        Assert.Equal("IMG [2023] 2024.txt", N("IMG 2023 2024.txt", new ReplaceTextRule { Find = @"\d+", Replacement = "[$0]", UseRegex = true, Occurrence = ReplaceOccurrence.First }));
    }

    [Fact]
    public void Insert_from_the_end_or_around_a_text()
    {
        Assert.Equal("abcd-ef.txt", N("abcdef.txt", new InsertTextRule { Text = "-", Position = InsertPosition.BeforeLastCharacters, Index = 2 }));
        Assert.Equal("abXcdef.txt", N("abcdef.txt", new InsertTextRule { Text = "X", Position = InsertPosition.BeforeText, Anchor = "CD" }));
        Assert.Equal("abcdXef.txt", N("abcdef.txt", new InsertTextRule { Text = "X", Position = InsertPosition.AfterText, Anchor = "cd" }));
        Assert.Equal("abcdef.txt", N("abcdef.txt", new InsertTextRule { Text = "X", Position = InsertPosition.AfterText, Anchor = "zz" }));
    }

    [Theory]
    [InlineData(RemoveMode.Symbols, "IMG_0042 (copia)!", "IMG0042 copia")]
    [InlineData(RemoveMode.Digits, "Track 01 Song", "Track Song")]
    [InlineData(RemoveMode.Letters, "Foto 2024 x", "2024")]
    [InlineData(RemoveMode.AllButDigits, "IMG_0042", "0042")]
    [InlineData(RemoveMode.Uppercase, "IMG Copia", "opia")]
    [InlineData(RemoveMode.Lowercase, "IMG Copia", "IMG C")]
    public void Remove_characters_by_type(RemoveMode mode, string stem, string expected) =>
        Assert.Equal(expected + ".txt", N(stem + ".txt", new RemoveCharactersRule { Mode = mode }));

    [Fact]
    public void Remove_a_list_of_characters()
    {
        Assert.Equal("a b c.txt", N("a (b) #c.txt", new RemoveCharactersRule { Mode = RemoveMode.CharacterList, Characters = "()#" }));
    }

    [Fact]
    public void Invert_case()
    {
        Assert.Equal("cIAO mondo.TXT", N("Ciao MONDO.TXT", new ChangeCaseRule { Mode = CaseMode.Invert }));
    }

    // ---- Salvataggio e schede

    [Fact]
    public void New_rules_round_trip_through_json()
    {
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var options = new BatchRenameOptions
        {
            Rules =
            [
                new MoveTextRule { Find = "x", Target = MoveTarget.Start },
                new SwapPartsRule { Separator = "_", Occurrence = 2 },
                new RenumberRule { Mode = RenumberMode.Add, Add = -3 },
                new TrimRule { Characters = "#", Where = TrimWhere.End },
                new NameListRule { Names = "a\nb" },
                new ReplaceListRule { Pairs = [new("a", "b")], UseRegex = true },
                new ReplaceTextRule { Find = "a", Occurrence = ReplaceOccurrence.Last },
                new InsertTextRule { Position = InsertPosition.AfterText, Anchor = "k" },
                new RemoveCharactersRule { Mode = RemoveMode.CharacterList, Characters = "!" },
            ],
        };
        var back = JsonSerializer.Deserialize<BatchRenameOptions>(JsonSerializer.Serialize(options, json), json)!;
        Assert.Equal(MoveTarget.Start, Assert.IsType<MoveTextRule>(back.Rules[0]).Target);
        Assert.Equal(2, Assert.IsType<SwapPartsRule>(back.Rules[1]).Occurrence);
        Assert.Equal(-3, Assert.IsType<RenumberRule>(back.Rules[2]).Add);
        Assert.Equal(TrimWhere.End, Assert.IsType<TrimRule>(back.Rules[3]).Where);
        Assert.Equal("a\nb", Assert.IsType<NameListRule>(back.Rules[4]).Names);
        Assert.Equal(new ReplacePair("a", "b"), Assert.Single(Assert.IsType<ReplaceListRule>(back.Rules[5]).Pairs));
        Assert.Equal(ReplaceOccurrence.Last, Assert.IsType<ReplaceTextRule>(back.Rules[6]).Occurrence);
        Assert.Equal("k", Assert.IsType<InsertTextRule>(back.Rules[7]).Anchor);
        Assert.Equal("!", Assert.IsType<RemoveCharactersRule>(back.Rules[8]).Characters);
    }

    [Fact]
    public void Every_rule_card_survives_a_save_and_reload()
    {
        foreach (var kind in BatchRenameViewModel.RuleKinds)
        {
            var rule = kind.Create().ToRule();
            var again = BatchRuleViewModel.Create(rule).ToRule();
            Assert.Equal(rule.GetType(), again.GetType());
            if (rule is ReplaceListRule list)
            {
                Assert.Equal(list.Pairs, ((ReplaceListRule)again).Pairs);
            }
            else
            {
                Assert.Equal(rule, again);
            }
        }
    }

    [Fact]
    public void Replace_list_card_edits_pairs_and_refreshes_the_preview()
    {
        var batch = new BatchRenameViewModel(new BatchRenameOptions());
        var changes = 0;
        batch.RulesChanged += (_, _) => changes++;
        batch.AddRuleCommand.Execute("sostituzioniMultiple");
        var card = Assert.IsType<ReplaceListRuleViewModel>(batch.Rules[0]);

        card.Pairs[0].Find = "a";
        card.Pairs[0].Replacement = "b";
        card.AddPairCommand.Execute(null);
        card.Pairs[1].Find = "c";
        var before = changes;
        card.Pairs[1].RemoveCommand.Execute(null);
        Assert.True(changes > before);

        var rule = Assert.IsType<ReplaceListRule>(Assert.Single(batch.ToOptions().Rules));
        Assert.Equal([new ReplacePair("a", "b")], rule.Pairs);

        card.UseRegex = true;
        card.Pairs[0].Find = "([";
        Assert.True(card.HasError);
    }

    [Fact]
    public void Name_list_card_fills_with_the_current_names()
    {
        var batch = new BatchRenameViewModel(new BatchRenameOptions()) { CurrentNames = () => ["Foto 1", "Foto 2"] };
        batch.AddRuleCommand.Execute("elenco");
        var card = Assert.IsType<NameListRuleViewModel>(batch.Rules[0]);
        card.FillWithCurrentNamesCommand.Execute(null);
        Assert.Equal("Foto 1\nFoto 2", card.Names);
    }
}
