using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using PyPreviewHost2.Core;
using PyPreviewHost2.Highlighting;
using static PyPreviewHost2.Highlighting.HighlightingBuilder;

namespace PyPreviewHost2
{
    /// <summary>
    /// Provides AvalonEdit highlighting definitions. Definitions are generated from
    /// <see cref="LanguageSpecs"/> so every language uses one consistent dark palette.
    /// These are lexical definitions only (no semantic analysis).
    /// </summary>
    internal static class SyntaxHighlighter
    {
        private static readonly Dictionary<string, IHighlightingDefinition> Cache =
            new Dictionary<string, IHighlightingDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Errors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new object();

        /// <summary>Original API: returns the definition for a file path, or null if none.</summary>
        public static IHighlightingDefinition ForFile(string path)
        {
            FormatInfo info = FormatCatalog.Lookup(Path.GetFileName(path),
                (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant());
            return info == null ? null : ForLanguage(info.LanguageId);
        }

        public static IHighlightingDefinition ForLanguage(string languageId)
        {
            IHighlightingDefinition def;
            string error;
            return TryGet(languageId, out def, out error) ? def : null;
        }

        public static bool TryGet(string languageId, out IHighlightingDefinition definition, out string error)
        {
            definition = null;
            error = null;
            if (string.IsNullOrEmpty(languageId)) return false;

            lock (Gate)
            {
                if (Cache.TryGetValue(languageId, out definition)) return true;
                if (Errors.TryGetValue(languageId, out error)) return false;

                LanguageSpec spec = LanguageSpecs.Find(languageId);
                if (spec == null)
                {
                    error = "No highlighting definition for '" + languageId + "'.";
                    Errors[languageId] = error;
                    return false;
                }
                try
                {
                    definition = HighlightingBuilder.Build(spec);
                    Cache[languageId] = definition;
                    return true;
                }
                catch (Exception ex)
                {
                    definition = null;
                    error = "Highlighting definition '" + languageId + "' failed to load: " + ex.Message;
                    Errors[languageId] = error;
                    return false;
                }
            }
        }
    }
}

namespace PyPreviewHost2.Highlighting
{
    /// <summary>Declarative description of a language's lexical highlighting.</summary>
    internal sealed class LanguageSpec
    {
        public string Id;
        public string DisplayName;
        public string Keywords = "";
        public string Types = "";
        public string Literals = "";
        /// <summary>Space-separated line-comment prefixes, e.g. "// #".</summary>
        public string LineComments = "";
        /// <summary>Pairs "begin end" separated by '|', e.g. "/* */|&lt;# #&gt;".</summary>
        public string BlockComments = "";
        /// <summary>Space-separated single-line quote characters (with escape character).</summary>
        public string Strings = "\" '";
        /// <summary>Single-line quotes without escapes.</summary>
        public string RawStrings = "";
        /// <summary>Multi-line delimiters (begin = end) with escapes.</summary>
        public string MultilineStrings = "";
        /// <summary>Multi-line delimiters (begin = end) without escapes.</summary>
        public string RawMultilineStrings = "";
        public string EscapeChar = "\\";
        /// <summary>Number regex; null = default, "" = none.</summary>
        public string Numbers;
        public bool IgnoreCase;
        public bool Operators;
        /// <summary>Raw XSHD fragments placed before / after the keyword rules.</summary>
        public string Pre = "";
        public string Post = "";
    }

    /// <summary>Generates XSHD documents from language specs and loads them with AvalonEdit.</summary>
    internal static class HighlightingBuilder
    {
        // Dark palette shared by all languages (VS Code "Dark+"-like).
        private static readonly string[][] Palette =
        {
            new[] { "Comment", "#6A9955" },  new[] { "String", "#CE9178" },  new[] { "Number", "#B5CEA8" },
            new[] { "Keyword", "#569CD6" },  new[] { "Type", "#4EC9B0" },    new[] { "Literal", "#4FC1FF" },
            new[] { "Preproc", "#C586C0" },  new[] { "Key", "#9CDCFE" },     new[] { "Tag", "#569CD6" },
            new[] { "Attr", "#9CDCFE" },     new[] { "Entity", "#D7BA7D" },  new[] { "Heading", "#569CD6" },
            new[] { "Added", "#B5CEA8" },    new[] { "Removed", "#F48771" }, new[] { "Hunk", "#C586C0" },
            new[] { "Variable", "#9CDCFE" }, new[] { "Label", "#DCDCAA" },   new[] { "Warn", "#D7BA7D" },
            new[] { "Error", "#F48771" },    new[] { "Info", "#569CD6" },    new[] { "Link", "#3794FF" },
            new[] { "Operator", "#C8AE74" }
        };

        public const string DefaultNumbers =
            @"\b(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?)[uUlLfFdDmM]*\b";
        public const string OperatorRegex = @"[+\-*/%=<>!&|^~?]+";

        public static IHighlightingDefinition Build(LanguageSpec spec)
        {
            using (var sr = new StringReader(BuildXml(spec)))
            using (var xr = XmlReader.Create(sr))
            {
                return HighlightingLoader.Load(xr, HighlightingManager.Instance);
            }
        }

        internal static string Esc(string s)
        {
            return s == null ? null : SecurityElement.Escape(s);
        }

        /// <summary>Rule: regex coloured with a palette colour. The regex must not match the empty string.</summary>
        internal static string R(string color, string regex)
        {
            return "<Rule color=\"" + color + "\">" + Esc(regex) + "</Rule>\n";
        }

        /// <summary>Span from begin to end (regexes). end == null: until end of line.</summary>
        internal static string S(string color, string begin, string end, bool multiline, string escape = null)
        {
            var sb = new StringBuilder();
            sb.Append("<Span color=\"").Append(color).Append("\"");
            if (multiline) sb.Append(" multiline=\"true\"");
            if (end == null)
            {
                sb.Append(" begin=\"").Append(Esc(begin)).Append("\" />\n");
                return sb.ToString();
            }
            sb.Append(">\n<Begin>").Append(Esc(begin)).Append("</Begin>\n<End>").Append(Esc(end)).Append("</End>\n");
            if (!string.IsNullOrEmpty(escape))
                sb.Append("<RuleSet><Span begin=\"").Append(Esc(Regex.Escape(escape))).Append("\" end=\".\" /></RuleSet>\n");
            sb.Append("</Span>\n");
            return sb.ToString();
        }

        private static string[] Split(string s, char sep)
        {
            return (s ?? string.Empty).Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static void AppendWords(StringBuilder sb, string color, string words)
        {
            string[] w = Split(words, ' ').Distinct().ToArray();
            if (w.Length == 0) return;
            sb.Append("<Keywords color=\"").Append(color).Append("\">\n");
            foreach (string word in w) sb.Append("<Word>").Append(Esc(word)).Append("</Word>\n");
            sb.Append("</Keywords>\n");
        }

        public static string BuildXml(LanguageSpec s)
        {
            var sb = new StringBuilder();
            sb.Append("<SyntaxDefinition name=\"").Append(Esc(s.DisplayName))
              .Append("\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">\n");
            foreach (string[] c in Palette)
                sb.Append("<Color name=\"").Append(c[0]).Append("\" foreground=\"").Append(c[1]).Append("\" />\n");
            sb.Append("<RuleSet ignoreCase=\"").Append(s.IgnoreCase ? "true" : "false").Append("\">\n");

            sb.Append(s.Pre);

            foreach (string pair in Split(s.BlockComments, '|'))
            {
                string[] p = pair.Split(' ');
                sb.Append(S("Comment", Regex.Escape(p[0]), Regex.Escape(p[1]), true));
            }
            foreach (string lc in Split(s.LineComments, ' '))
                sb.Append(S("Comment", Regex.Escape(lc), null, false));
            foreach (string d in Split(s.MultilineStrings, ' '))
                sb.Append(S("String", Regex.Escape(d), Regex.Escape(d), true, s.EscapeChar));
            foreach (string d in Split(s.RawMultilineStrings, ' '))
                sb.Append(S("String", Regex.Escape(d), Regex.Escape(d), true));
            foreach (string q in Split(s.Strings, ' '))
                sb.Append(S("String", Regex.Escape(q), Regex.Escape(q), false, s.EscapeChar));
            foreach (string q in Split(s.RawStrings, ' '))
                sb.Append(S("String", Regex.Escape(q), Regex.Escape(q), false));

            AppendWords(sb, "Keyword", s.Keywords);
            AppendWords(sb, "Type", s.Types);
            AppendWords(sb, "Literal", s.Literals);
            sb.Append(s.Post);

            string numbers = s.Numbers == null ? DefaultNumbers : s.Numbers;
            if (numbers.Length > 0) sb.Append(R("Number", numbers));
            if (s.Operators) sb.Append(R("Operator", OperatorRegex));

            sb.Append("</RuleSet>\n</SyntaxDefinition>\n");
            return sb.ToString();
        }
    }

    /// <summary>All supported highlighting languages. Every id must be loadable; see README.</summary>
    internal static class LanguageSpecs
    {
        private static readonly Dictionary<string, LanguageSpec> Specs =
            new Dictionary<string, LanguageSpec>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<LanguageSpec> All { get { return Specs.Values; } }

        public static LanguageSpec Find(string id)
        {
            LanguageSpec s;
            return id != null && Specs.TryGetValue(id, out s) ? s : null;
        }

        public static string DisplayName(string id)
        {
            LanguageSpec s = Find(id);
            return s != null ? s.DisplayName : id;
        }

        private static void Add(LanguageSpec s)
        {
            Specs[s.Id] = s;
        }

        private const string CKeywords =
            "auto break case const continue default do else enum extern for goto if inline register restrict return sizeof static struct switch typedef union volatile while";
        private const string CTypes =
            "void char short int long float double signed unsigned bool size_t ssize_t ptrdiff_t intptr_t uintptr_t int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t wchar_t FILE";
        private const string Preprocessor = @"^\s*\#\s*\w+";

        private static LanguageSpec XmlLike(string id, string name, bool ignoreCase)
        {
            return new LanguageSpec
            {
                Id = id, DisplayName = name, IgnoreCase = ignoreCase, Strings = "", Numbers = "",
                Pre = S("Comment", "<!--", "-->", true)
                    + S("String", @"<!\[CDATA\[", @"\]\]>", true)
                    + S("Preproc", @"<\?", @"\?>", true)
                    + S("Preproc", "<!", ">", true)
                    + "<Span color=\"Tag\" multiline=\"true\">\n<Begin>" + Esc(@"</?[A-Za-z_:][\w:.\-]*") + "</Begin>\n<End>"
                    + Esc("/?>") + "</End>\n<RuleSet>\n"
                    + S("String", "\"", "\"", true) + S("String", "'", "'", true)
                    + R("Attr", @"[\w:.\-]+(?=\s*=)")
                    + "</RuleSet>\n</Span>\n"
                    + R("Entity", @"&[\w#]+;")
            };
        }

        static LanguageSpecs()
        {
            Add(new LanguageSpec
            {
                Id = "python", DisplayName = "Python", Operators = true,
                Keywords = "and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield",
                Types = "int float str bool list dict set tuple bytes bytearray object type range frozenset complex memoryview",
                Literals = "True False None",
                LineComments = "#", MultilineStrings = "\"\"\" '''",
                Pre = R("Preproc", @"^\s*@[\w.]+")
            });

            Add(new LanguageSpec
            {
                Id = "c", DisplayName = "C", Operators = true,
                Keywords = CKeywords + " _Alignas _Alignof _Atomic _Complex _Generic _Noreturn _Static_assert _Thread_local",
                Types = CTypes, Literals = "NULL true false",
                LineComments = "//", BlockComments = "/* */", Pre = R("Preproc", Preprocessor)
            });

            Add(new LanguageSpec
            {
                Id = "cpp", DisplayName = "C/C++", Operators = true,
                Keywords = CKeywords + " alignas alignof asm catch class concept consteval constexpr constinit const_cast co_await co_return co_yield decltype delete dynamic_cast explicit export final friend mutable namespace new noexcept operator override private protected public reinterpret_cast requires static_assert static_cast template this thread_local throw try typeid typename using virtual and or not xor",
                Types = CTypes + " string wstring vector map set array pair tuple unique_ptr shared_ptr weak_ptr optional variant char8_t char16_t char32_t nullptr_t",
                Literals = "NULL true false nullptr",
                LineComments = "//", BlockComments = "/* */", Pre = R("Preproc", Preprocessor)
            });

            Add(new LanguageSpec
            {
                Id = "csharp", DisplayName = "C#", Operators = true,
                Keywords = "abstract as base break case catch checked class const continue default delegate do else enum event explicit extern finally fixed for foreach goto if implicit in interface internal is lock namespace new operator out override params private protected public readonly ref return sealed sizeof stackalloc static struct switch throw try typeof unchecked unsafe using virtual volatile while async await var partial yield nameof record init",
                Types = "bool byte char decimal double float int long object sbyte short string uint ulong ushort void nint nuint dynamic",
                Literals = "true false null",
                LineComments = "//", BlockComments = "/* */", MultilineStrings = "\"\"\"",
                Pre = R("Preproc", Preprocessor)
                    + "<Span color=\"String\" multiline=\"true\">\n<Begin>" + Esc("@\"") + "</Begin>\n<End>" + Esc("\"(?!\")")
                    + "</End>\n<RuleSet>" + R("String", "\"\"") + "</RuleSet>\n</Span>\n"
            });

            Add(new LanguageSpec
            {
                Id = "java", DisplayName = "Java", Operators = true,
                Keywords = "abstract assert break case catch class const continue default do else enum extends final finally for goto if implements import instanceof interface native new package private protected public return static strictfp super switch synchronized this throw throws transient try volatile while record sealed permits var yield",
                Types = "boolean byte char double float int long short void String Object",
                Literals = "true false null",
                LineComments = "//", BlockComments = "/* */", MultilineStrings = "\"\"\"",
                Pre = R("Preproc", @"@\w+")
            });

            const string JsKeywords = "async await break case catch class const continue debugger default delete do else export extends finally for function if import in instanceof let new return static super switch this throw try typeof var void while with yield";
            const string JsTypes = "Array Object String Number Boolean Map Set Promise Symbol Date RegExp Error JSON Math";

            Add(new LanguageSpec
            {
                Id = "javascript", DisplayName = "JavaScript", Operators = true,
                Keywords = JsKeywords, Types = JsTypes, Literals = "true false null undefined NaN Infinity",
                LineComments = "//", BlockComments = "/* */", MultilineStrings = "`"
            });

            Add(new LanguageSpec
            {
                Id = "typescript", DisplayName = "TypeScript", Operators = true,
                Keywords = JsKeywords + " abstract as declare enum implements interface keyof namespace private protected public readonly type unknown never is infer satisfies override module",
                Types = JsTypes + " any number string boolean void object bigint symbol",
                Literals = "true false null undefined NaN Infinity",
                LineComments = "//", BlockComments = "/* */", MultilineStrings = "`",
                Pre = R("Preproc", @"@\w+")
            });

            Add(XmlLike("xml", "XML", false));
            var html = XmlLike("html", "HTML (tags and attributes only)", true);
            Add(html);

            Add(new LanguageSpec
            {
                Id = "css", DisplayName = "CSS", Types = "",
                BlockComments = "/* */",
                Numbers = @"(?<![\w#\-])-?\d*\.?\d+(?:px|em|rem|%|vh|vw|vmin|vmax|pt|cm|mm|in|s|ms|deg|fr|ch|ex)?",
                Pre = R("Number", @"\#[0-9A-Fa-f]{3,8}\b")
                    + R("Preproc", @"@[\w-]+")
                    + R("Literal", @"!important")
                    + R("Variable", @"--[\w-]+")
                    + R("Key", @"^\s+[\w-]+(?=\s*:)")
                    + R("Type", @"[.\#][A-Za-z_][\w-]*")
            });

            Add(new LanguageSpec
            {
                Id = "scss", DisplayName = "SCSS / Sass / Less",
                LineComments = "//", BlockComments = "/* */",
                Numbers = @"(?<![\w#\-])-?\d*\.?\d+(?:px|em|rem|%|vh|vw|vmin|vmax|pt|cm|mm|in|s|ms|deg|fr|ch|ex)?",
                Pre = R("Number", @"\#[0-9A-Fa-f]{3,8}\b")
                    + R("Preproc", @"@[\w-]+")
                    + R("Literal", @"!(?:important|default)")
                    + R("Variable", @"(?:--|\$)[\w-]+")
                    + R("Key", @"^\s+[\w-]+(?=\s*:)")
                    + R("Type", @"[.\#][A-Za-z_][\w-]*")
            });

            Add(new LanguageSpec
            {
                Id = "sql", DisplayName = "SQL", IgnoreCase = true, Operators = true,
                Keywords = "add all alter and any as asc backup between by case check column constraint create database default delete desc distinct drop exec exists foreign from full group having in index inner insert into is join key left like limit not offset on or order outer primary procedure replace right rownum select set table top truncate union unique update values view where with begin end commit rollback transaction declare if else while return returns function trigger grant revoke cascade using over partition rows range window",
                Types = "int integer bigint smallint tinyint decimal numeric float real double varchar nvarchar char nchar text date datetime timestamp time boolean bit blob binary uuid serial json",
                Literals = "true false null",
                LineComments = "--", BlockComments = "/* */", Strings = "\"", EscapeChar = "", RawStrings = "'"
            });

            Add(new LanguageSpec
            {
                Id = "shell", DisplayName = "Shell script", Strings = "\"", RawStrings = "'",
                Keywords = "if then else elif fi case esac for select while until do done in function time coproc return exit break continue",
                Types = "echo cd pwd export unset set shift read printf test source alias unalias eval exec trap wait local declare typeset readonly let ulimit umask kill jobs bg fg true false",
                Pre = R("Variable", @"\$(?:\{[^}\n]*\}|[A-Za-z_]\w*|[0-9@*?!$\#])")
                    + S("Comment", @"(?<![$\w\\])\#", null, false)
            });

            Add(new LanguageSpec
            {
                Id = "powershell", DisplayName = "PowerShell", IgnoreCase = true, Strings = "\"", EscapeChar = "`", RawStrings = "'",
                Keywords = "begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using var while workflow",
                Types = "bool byte char datetime decimal double float guid hashtable int int32 int64 long object psobject regex single string timespan uint32 xml array void",
                LineComments = "#", BlockComments = "<# #>",
                Pre = R("Literal", @"\$(?:true|false|null)\b")
                    + R("Variable", @"\$(?:\w+:)?\w+|\$\{[^}\n]*\}"),
                Post = R("Type", @"\b[A-Za-z]+-[A-Za-z]+\b") + R("Attr", @"(?<=\s)-[A-Za-z]\w*")
            });

            Add(new LanguageSpec
            {
                Id = "batch", DisplayName = "Batch file", IgnoreCase = true, Strings = "\"", EscapeChar = "", Numbers = "",
                Keywords = "echo set setlocal endlocal if else for in do goto call exit not exist defined errorlevel equ neq lss leq gtr geq cd chdir md mkdir rd rmdir copy xcopy move del erase type pause cls title start shift pushd popd",
                Pre = R("Comment", @"^\s*@?rem\b.*$")
                    + S("Comment", @"^\s*::", null, false)
                    + R("Variable", @"%[^%\s]+%|%[0-9*]|![^!\s]+!")
                    + R("Label", @"^\s*:[\w.\-]+")
            });

            Add(new LanguageSpec
            {
                Id = "rust", DisplayName = "Rust", Operators = true, Strings = "\"",
                Keywords = "as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait type unsafe use where while union macro_rules",
                Types = "i8 i16 i32 i64 i128 isize u8 u16 u32 u64 u128 usize f32 f64 bool char str String Vec Option Result Box Rc Arc",
                Literals = "true false None Some Ok Err",
                LineComments = "//", BlockComments = "/* */",
                Pre = R("Preproc", @"\#!?\[[^\]\n]*\]")
                    + R("String", @"'(?:\\.|[^\\'])'")
                    + R("Preproc", @"\b[A-Za-z_]\w*!(?=[\s(\[{])")
            });

            Add(new LanguageSpec
            {
                Id = "go", DisplayName = "Go", Operators = true, Strings = "\" '", RawMultilineStrings = "`",
                Keywords = "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var",
                Types = "bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any",
                Literals = "true false nil iota",
                LineComments = "//", BlockComments = "/* */"
            });

            Add(new LanguageSpec
            {
                Id = "php", DisplayName = "PHP", IgnoreCase = true, Operators = true,
                Keywords = "abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile eval exit extends final finally fn for foreach function global goto if implements include include_once instanceof insteadof interface isset list match namespace new or print private protected public readonly require require_once return static switch throw trait try unset use var while xor yield",
                Types = "int float string bool array object mixed void iterable self parent",
                Literals = "true false null",
                LineComments = "// #", BlockComments = "/* */",
                Pre = R("Variable", @"\$\w+") + R("Preproc", @"<\?php|<\?=|\?>")
            });

            Add(new LanguageSpec
            {
                Id = "ruby", DisplayName = "Ruby", Strings = "\" ' `", Operators = true,
                Keywords = "alias and begin break case class def do else elsif end ensure for if in module next not or redo rescue retry return self super then undef unless until when while yield require require_relative include extend attr_accessor attr_reader attr_writer private protected public raise lambda proc",
                Literals = "true false nil",
                LineComments = "#",
                Pre = S("Comment", @"^=begin", @"^=end", true)
                    + R("Variable", @"@{1,2}\w+|\$\w+")
                    + R("Literal", @"(?<![:\w]):[A-Za-z_]\w*[?!]?")
            });

            Add(new LanguageSpec
            {
                Id = "lua", DisplayName = "Lua", Operators = true,
                Keywords = "and break do else elseif end for function goto if in local not or repeat return then until while",
                Types = "self", Literals = "true false nil",
                LineComments = "--",
                Pre = S("Comment", @"--\[\[", @"\]\]", true) + S("String", @"\[\[", @"\]\]", true)
            });

            Add(new LanguageSpec
            {
                Id = "perl", DisplayName = "Perl", Operators = true,
                Keywords = "if elsif else unless while until for foreach do last next redo return sub my our local use no package require BEGIN END and or not xor eq ne lt gt le ge cmp x print say die warn defined undef ref bless",
                LineComments = "#",
                Pre = S("Comment", @"^=[a-zA-Z]", @"^=cut\b.*$", true) + R("Variable", @"[$@%]\w+")
            });

            Add(new LanguageSpec
            {
                Id = "swift", DisplayName = "Swift", Operators = true, MultilineStrings = "\"\"\"", Strings = "\"",
                Keywords = "associatedtype break case catch class continue default defer deinit do else enum extension fallthrough fileprivate for func guard if import in init inout internal is let open operator private protocol public repeat rethrows return self Self static struct subscript super switch throw throws try typealias var where while async await actor some any",
                Types = "Int Int8 Int16 Int32 Int64 UInt UInt8 UInt16 UInt32 UInt64 Float Double Bool String Character Array Dictionary Set Optional Void Any AnyObject",
                Literals = "true false nil",
                LineComments = "//", BlockComments = "/* */",
                Pre = R("Preproc", @"[@\#]\w+")
            });

            Add(new LanguageSpec
            {
                Id = "kotlin", DisplayName = "Kotlin", Operators = true, MultilineStrings = "\"\"\"",
                Keywords = "abstract annotation as break by catch class companion const constructor continue crossinline data do else enum expect external final finally for fun if in infix init inline inner interface internal is lateinit noinline object open operator out override package private protected public reified return sealed super suspend tailrec this throw try typealias typeof val var vararg when where while",
                Types = "Int Long Short Byte Float Double Boolean Char String Unit Any Nothing Array List Map Set",
                Literals = "true false null",
                LineComments = "//", BlockComments = "/* */",
                Pre = R("Preproc", @"@\w+")
            });

            Add(new LanguageSpec
            {
                Id = "dart", DisplayName = "Dart", Operators = true, MultilineStrings = "\"\"\" '''",
                Keywords = "abstract as assert async await break case catch class const continue covariant default deferred do dynamic else enum export extends extension external factory final finally for Function get hide if implements import in interface is late library mixin new on operator part required rethrow return sealed set show static super switch sync this throw try typedef var void while with yield",
                Types = "int double num bool String List Map Set Object dynamic void Future Stream Iterable",
                Literals = "true false null",
                LineComments = "//", BlockComments = "/* */",
                Pre = R("Preproc", @"@\w+")
            });

            Add(new LanguageSpec
            {
                Id = "r", DisplayName = "R", Operators = true,
                Keywords = "if else repeat while function for in next break return library require",
                Literals = "TRUE FALSE NULL NA NA_integer_ NA_real_ NA_character_ Inf NaN",
                LineComments = "#",
                Pre = R("Preproc", @"<<-|<-|->>|->|%[^%\s]*%|\|>")
            });

            Add(new LanguageSpec
            {
                Id = "matlab", DisplayName = "MATLAB/Octave", Strings = "\"", Operators = true,
                Keywords = "break case catch classdef continue else elseif end for function global if otherwise parfor persistent return spmd switch try while",
                Literals = "true false pi inf nan eps",
                LineComments = "%",
                Pre = R("String", @"(?<![\w)\]}.'])'(?:[^'\n]|'')*'")
            });

            Add(new LanguageSpec
            {
                Id = "asm", DisplayName = "Assembly", IgnoreCase = true, Strings = "\" '",
                Keywords = "mov push pop call ret jmp je jne jz jnz jg jge jl jle ja jae jb jbe cmp add sub mul imul div idiv inc dec lea and or xor not shl shr sal sar test nop int syscall leave rep movs stos cld std cli sti hlt ldr str bl b bx cbz cbnz",
                LineComments = "; //", BlockComments = "/* */",
                Numbers = @"\b(?:0[xX][0-9a-fA-F]+|[0-9a-fA-F]+[hH]|\d+)\b",
                Pre = R("Type", @"\b(?:[re]?[abcd]x|[re]?[sd]il?|[re]?[sb]pl?|[abcd][lh]|r(?:[89]|1[0-5])[dwb]?|[xyz]mm\d+|[xw]\d{1,2}|sp|lr|pc)\b")
                    + R("Preproc", @"^\s*\.\w+")
                    + R("Label", @"^\s*[\w.$@]+(?=:)")
            });

            Add(new LanguageSpec
            {
                Id = "json", DisplayName = "JSON", Strings = "", Literals = "true false null",
                Numbers = @"-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b",
                Pre = S("Key", "\"(?=(?:[^\"\\\\\\n]|\\\\.)*\"\\s*:)", "\"", false, "\\")
                    + S("String", "\"", "\"", false, "\\")
            });

            Add(new LanguageSpec
            {
                Id = "jsonc", DisplayName = "JSON with comments", Strings = "'", Literals = "true false null",
                LineComments = "//", BlockComments = "/* */",
                Numbers = @"-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b",
                Pre = S("Key", "\"(?=(?:[^\"\\\\\\n]|\\\\.)*\"\\s*:)", "\"", false, "\\")
                    + S("String", "\"", "\"", false, "\\")
            });

            Add(new LanguageSpec
            {
                Id = "yaml", DisplayName = "YAML", Strings = "\"", RawStrings = "'",
                Literals = "true false null True False Null",
                Pre = R("Comment", @"(?<![^\s])\#.*$")
                    + R("Key", @"(?<=^\s*(?:-\s+)?)[\w.\-/]+(?=\s*:(?:\s|$))")
                    + R("Label", @"[&*][\w-]+")
                    + R("Preproc", @"^(?:---|\.\.\.)(?=\s|$)")
            });

            Add(new LanguageSpec
            {
                Id = "toml", DisplayName = "TOML", Strings = "\"", RawStrings = "'",
                MultilineStrings = "\"\"\"", RawMultilineStrings = "'''",
                Literals = "true false inf nan",
                Pre = R("Comment", @"\#.*$")
                    + R("Type", @"^\s*\[\[?[^\]\n]*\]\]?")
                    + R("Key", @"(?<=^\s*)[\w.\-]+(?=\s*=)")
                    + R("Number", @"\d{4}-\d{2}-\d{2}(?:[Tt\x20][\d:.]+(?:Z|[+-]\d{2}:\d{2})?)?")
            });

            Add(new LanguageSpec
            {
                Id = "ini", DisplayName = "INI / configuration", Strings = "\"",
                Keywords = "export", Literals = "true false yes no on off",
                Pre = R("Comment", @"^\s*[;\#].*$")
                    + R("Type", @"^\s*\[[^\]\n]*\]")
                    + R("Key", @"(?<=^\s*)[^=;\#\[\s][^=\n]*?(?=\s*=)")
            });

            Add(new LanguageSpec
            {
                Id = "markdown", DisplayName = "Markdown", Strings = "", Numbers = "",
                Pre = S("String", @"^\s*(?:```|~~~)", @"^\s*(?:```|~~~)", true)
                    + S("Comment", "<!--", "-->", true)
                    + R("Heading", @"^\s{0,3}\#{1,6}\s.*$")
                    + R("Comment", @"^\s*>.*$")
                    + R("Operator", @"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$")
                    + R("Operator", @"^\s*(?:[-*+]|\d+[.)])(?=\s)")
                    + R("String", @"`[^`\n]+`")
                    + R("Label", @"(?:\*\*|__)(?=\S)[^\n]*?\S(?:\*\*|__)")
                    + R("Link", @"\[[^\]\n]*\]\([^)\n]*\)")
                    + R("Link", @"https?://[^\s)>\]]+")
            });

            Add(new LanguageSpec
            {
                Id = "diff", DisplayName = "Diff / patch", Strings = "", Numbers = "",
                Pre = R("Added", @"^\+(?!\+\+).*$")
                    + R("Removed", @"^-(?!--).*$")
                    + R("Hunk", @"^@@.*$")
                    + R("Label", @"^(?:diff\s|index\s|---|\+\+\+|new file|deleted file|similarity index|rename\s|old mode|new mode|Binary files).*$")
            });

            Add(new LanguageSpec
            {
                Id = "http", DisplayName = "HTTP request file", Strings = "\"",
                Keywords = "GET POST PUT PATCH DELETE HEAD OPTIONS TRACE CONNECT",
                Pre = R("Heading", @"^\#{3,}.*$")
                    + R("Comment", @"^\s*(?:\#(?!\#\#)|//).*$")
                    + R("Literal", @"HTTP/[\d.]+")
                    + R("Variable", @"\{\{[^}\n]*\}\}")
                    + R("Variable", @"^\s*@\w+")
                    + R("Key", @"^[A-Za-z][\w-]*(?=:)")
            });

            Add(new LanguageSpec
            {
                Id = "graphql", DisplayName = "GraphQL", MultilineStrings = "\"\"\"", Strings = "\"",
                Keywords = "query mutation subscription fragment on type interface union enum input scalar schema extend implements directive repeatable",
                Types = "Int Float String Boolean ID", Literals = "true false null",
                LineComments = "#",
                Pre = R("Variable", @"\$\w+") + R("Preproc", @"@\w+")
            });

            Add(new LanguageSpec
            {
                Id = "proto", DisplayName = "Protocol Buffers",
                Keywords = "syntax package import option message enum service rpc returns oneof map repeated optional required reserved extensions extend stream group public weak to max",
                Types = "double float int32 int64 uint32 uint64 sint32 sint64 fixed32 fixed64 sfixed32 sfixed64 bool string bytes",
                Literals = "true false",
                LineComments = "//", BlockComments = "/* */"
            });

            Add(new LanguageSpec
            {
                Id = "tex", DisplayName = "LaTeX", Strings = "", Numbers = "",
                Pre = R("Comment", @"(?<!\\)%.*$")
                    + R("Preproc", @"\\(?:begin|end)\{[^}\n]*\}")
                    + R("Keyword", @"\\[A-Za-z@]+\*?")
                    + R("String", @"\$\$?[^$\n]*\$\$?")
            });

            Add(new LanguageSpec
            {
                Id = "bib", DisplayName = "BibTeX", Strings = "\"",
                Pre = R("Keyword", @"@\w+(?=\s*[{(])") + R("Key", @"\b\w+(?=\s*=)")
            });

            Add(new LanguageSpec
            {
                Id = "dockerfile", DisplayName = "Dockerfile", IgnoreCase = true,
                Keywords = "FROM RUN CMD LABEL EXPOSE ENV ADD COPY ENTRYPOINT VOLUME USER WORKDIR ARG ONBUILD STOPSIGNAL HEALTHCHECK SHELL AS MAINTAINER",
                LineComments = "#",
                Pre = R("Variable", @"\$\{?\w+\}?")
            });

            Add(new LanguageSpec
            {
                Id = "makefile", DisplayName = "Makefile",
                Keywords = "ifeq ifneq ifdef ifndef else endif include sinclude define endef export unexport override vpath .PHONY .DEFAULT",
                LineComments = "#",
                Pre = R("Variable", @"\$[({][^)}\n]*[)}]|\$[@<^?*%+|]|\$\w")
                    + R("Key", @"^\s*[A-Za-z_][\w.]*(?=\s*[:+?]?=)")
                    + R("Label", @"^[\w.%/\-]+(?=\s*:(?!=))")
            });

            Add(new LanguageSpec
            {
                Id = "cmake", DisplayName = "CMake", IgnoreCase = true, Strings = "\"",
                Keywords = "cmake_minimum_required project add_executable add_library add_subdirectory add_definitions add_compile_options add_custom_command add_custom_target target_link_libraries target_include_directories target_compile_definitions target_compile_options target_sources set unset option include include_directories link_directories link_libraries find_package find_library find_path find_program install configure_file file list string math message function endfunction macro endmacro if elseif else endif foreach endforeach while endwhile return break continue set_target_properties set_property get_property enable_testing add_test export",
                Literals = "ON OFF TRUE FALSE YES NO",
                LineComments = "#",
                Pre = R("Variable", @"\$(?:ENV)?\{[^}\n]*\}")
            });

            Add(new LanguageSpec
            {
                Id = "log", DisplayName = "Log file", Strings = "", Numbers = "",
                Pre = R("Number", @"\d{4}[-/]\d{2}[-/]\d{2}[T\x20]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?")
                    + R("Error", @"\b(?:ERROR|FATAL|CRITICAL|SEVERE|FAIL(?:ED|URE)?|Exception)\b")
                    + R("Warn", @"\bWARN(?:ING)?\b")
                    + R("Info", @"\b(?:INFO|NOTICE)\b")
                    + R("Comment", @"\b(?:DEBUG|TRACE|VERBOSE)\b")
            });

            Add(new LanguageSpec
            {
                Id = "ignore", DisplayName = "Ignore / attributes file", Strings = "", Numbers = "",
                Pre = R("Comment", @"^\s*\#.*$") + R("Operator", @"^!") + R("Operator", @"[*?\[\]]+")
            });
        }
    }
}