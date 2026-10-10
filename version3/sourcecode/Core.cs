using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PyPreviewHost2.Highlighting;

namespace PyPreviewHost2.Core
{
    // =====================================================================
    // Settings
    // =====================================================================

    /// <summary>
    /// Optional settings stored in %LOCALAPPDATA%\PyPreviewHost2\settings.ini (key=value lines).
    /// A missing or malformed file simply yields the defaults.
    /// </summary>
    internal sealed class AppSettings
    {
        public bool WordWrap { get; set; }
        public bool LineNumbers { get; set; }
        public double FontSize { get; set; }
        public int BytesPerRow { get; set; }
        public long MaxTextBytes { get; set; }
        public bool AutoResizeToParent { get; set; }
        public string FontFamilyName { get; set; }

        public AppSettings()
        {
            WordWrap = false;
            LineNumbers = false;
            FontSize = 14;
            BytesPerRow = 16;
            MaxTextBytes = 8L * 1024 * 1024;
            AutoResizeToParent = true;
            FontFamilyName = "Cascadia Mono, Consolas, Courier New";
        }

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PyPreviewHost2", "settings.ini");
            }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return Parse(File.ReadAllLines(SettingsPath));
            }
            catch { }
            return new AppSettings();
        }

        public static AppSettings Parse(IEnumerable<string> lines)
        {
            var s = new AppSettings();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();

                bool b;
                double d;
                long l;
                switch (key)
                {
                    case "wordwrap": if (bool.TryParse(value, out b)) s.WordWrap = b; break;
                    case "linenumbers": if (bool.TryParse(value, out b)) s.LineNumbers = b; break;
                    case "autoresizetoparent": if (bool.TryParse(value, out b)) s.AutoResizeToParent = b; break;
                    case "fontsize":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            s.FontSize = Math.Max(6, Math.Min(48, d));
                        break;
                    case "bytesperrow":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)
                            && (l == 8 || l == 16 || l == 24 || l == 32))
                            s.BytesPerRow = (int)l;
                        break;
                    case "maxtextbytes":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                            s.MaxTextBytes = Math.Max(64 * 1024, Math.Min(32L * 1024 * 1024, l));
                        break;
                    case "fontfamily":
                        if (value.Length > 0) s.FontFamilyName = value;
                        break;
                }
            }
            return s;
        }

        public void Save()
        {
            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                sb.AppendLine("; PyPreviewHost2 settings");
                sb.AppendLine("wordWrap=" + WordWrap.ToString().ToLowerInvariant());
                sb.AppendLine("lineNumbers=" + LineNumbers.ToString().ToLowerInvariant());
                sb.AppendLine("fontSize=" + FontSize.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("bytesPerRow=" + BytesPerRow.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxTextBytes=" + MaxTextBytes.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("autoResizeToParent=" + AutoResizeToParent.ToString().ToLowerInvariant());
                sb.AppendLine("fontFamily=" + FontFamilyName);
                File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }
    }

    // =====================================================================
    // Stream helpers and result model
    // =====================================================================

    internal static class StreamUtil
    {
        public static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        /// <summary>Opens a file read-only, tolerating other processes writing/deleting it.</summary>
        public static FileStream OpenShared(string path, FileOptions options)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, options);
        }
    }

    internal enum ReaderCategory
    {
        PlainText,
        SourceCode,
        StructuredText,
        Hex,
        SpecializedBinary,
        Unknown
    }

    internal enum ViewMode
    {
        Text,
        Hex
    }

    /// <summary>UI-independent outcome of reading a file.</summary>
    internal sealed class PreviewResult
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Extension { get; set; }
        public long FileSize { get; set; }

        public string ReaderName { get; set; }
        public ReaderCategory Category { get; set; }
        public string FormatName { get; set; }

        public ViewMode InitialView { get; set; }

        /// <summary>Decoded text or metadata summary. Null when only a hex view exists.</summary>
        public string Text { get; set; }
        public string LanguageId { get; set; }
        public bool HighlightingDisabled { get; set; }

        public string EncodingName { get; set; }
        public bool EncodingIsGuess { get; set; }
        public string LineEndings { get; set; }
        public bool IsPartial { get; set; }
        public bool IsEmpty { get; set; }
        public bool ContentLooksBinary { get; set; }

        public string Notice { get; set; }
        public string ErrorMessage { get; set; }

        public bool IsError { get { return ErrorMessage != null; } }

        public static PreviewResult Failure(string path, string message)
        {
            string name = path;
            try { name = Path.GetFileName(path); } catch { }
            return new PreviewResult
            {
                FilePath = path,
                FileName = name,
                Extension = string.Empty,
                ReaderName = "Error",
                Category = ReaderCategory.Unknown,
                ErrorMessage = message
            };
        }
    }

    /// <summary>First bytes of a file plus metadata, used by readers to decide whether they apply.</summary>
    internal sealed class FileProbe
    {
        public const int HeadSize = 32768;

        private TextDetection detection;

        public string FullPath { get; private set; }
        public string FileName { get; private set; }
        public string Extension { get; private set; }
        public long Length { get; private set; }
        public byte[] Head { get; private set; }
        public int HeadLength { get; private set; }

        public bool HeadIsTruncated { get { return Length > HeadLength; } }

        public TextDetection Detection
        {
            get
            {
                if (detection == null)
                    detection = TextSniffer.Analyze(Head, HeadLength, HeadIsTruncated);
                return detection;
            }
        }

        private FileProbe() { }

        public static FileProbe Open(string path)
        {
            using (FileStream fs = StreamUtil.OpenShared(path, FileOptions.SequentialScan))
            {
                long length = fs.Length;
                byte[] head = new byte[(int)Math.Min(length, HeadSize)];
                int read = StreamUtil.ReadFully(fs, head, 0, head.Length);
                return new FileProbe
                {
                    FullPath = path,
                    FileName = Path.GetFileName(path),
                    Extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant(),
                    Length = length,
                    Head = head,
                    HeadLength = read
                };
            }
        }
    }

    // =====================================================================
    // Text / binary detection and statistics
    // =====================================================================

    internal enum TextKind
    {
        Empty,
        Text,
        Binary
    }

    internal sealed class TextDetection
    {
        public TextKind Kind { get; set; }
        public Encoding Encoding { get; set; }
        public string EncodingName { get; set; }
        public int BomLength { get; set; }
        public bool IsGuess { get; set; }
        public string Note { get; set; }
        /// <summary>Bytes per code unit: 1 (UTF-8/legacy), 2 (UTF-16) or 4 (UTF-32).</summary>
        public int UnitSize { get; set; }
    }

    /// <summary>
    /// Deterministic text/binary and encoding detection.
    /// Order: (1) BOM; (2) NUL bytes / control-character density on the first 32 KiB decide binary,
    /// except BOM-less UTF-16 recognised by NUL placement; (3) strict UTF-8 validation of all supplied bytes
    /// (mostly-valid UTF-8 is still treated as UTF-8); (4) otherwise the system ANSI code page, flagged as a guess.
    /// </summary>
    internal static class TextSniffer
    {
        private const int SampleSize = 32768;

        public static TextDetection Analyze(byte[] data, int count, bool truncated)
        {
            if (count == 0)
            {
                return new TextDetection
                {
                    Kind = TextKind.Empty,
                    Encoding = new UTF8Encoding(false, false),
                    EncodingName = "n/a (empty file)",
                    UnitSize = 1
                };
            }

            if (count >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Bom(new UTF8Encoding(false, false), "UTF-8 with BOM", 3, 1);
            if (count >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0)
                return Bom(new UTF32Encoding(false, false, false), "UTF-32 LE with BOM", 4, 4);
            if (count >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xFE && data[3] == 0xFF)
                return Bom(new UTF32Encoding(true, false, false), "UTF-32 BE with BOM", 4, 4);
            if (count >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Bom(new UnicodeEncoding(false, false, false), "UTF-16 LE with BOM", 2, 2);
            if (count >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return Bom(new UnicodeEncoding(true, false, false), "UTF-16 BE with BOM", 2, 2);

            int sample = Math.Min(count, SampleSize);
            int nul = 0, zerosEven = 0, zerosOdd = 0, control = 0;
            for (int i = 0; i < sample; i++)
            {
                byte b = data[i];
                if (b == 0)
                {
                    nul++;
                    if ((i & 1) == 0) zerosEven++; else zerosOdd++;
                }
                else if ((b < 0x20 && b != 9 && b != 10 && b != 12 && b != 13 && b != 27) || b == 0x7F)
                {
                    control++;
                }
            }

            if (nul > 0)
            {
                int pairs = sample / 2;
                bool le = pairs >= 2 && zerosOdd * 10 >= pairs * 3 && zerosEven * 20 <= pairs && control * 50 <= sample;
                bool be = pairs >= 2 && zerosEven * 10 >= pairs * 3 && zerosOdd * 20 <= pairs && control * 50 <= sample;
                if (le)
                    return new TextDetection
                    {
                        Kind = TextKind.Text, Encoding = new UnicodeEncoding(false, false, false),
                        EncodingName = "UTF-16 LE (no BOM, detected from NUL pattern)", IsGuess = true, UnitSize = 2
                    };
                if (be)
                    return new TextDetection
                    {
                        Kind = TextKind.Text, Encoding = new UnicodeEncoding(true, false, false),
                        EncodingName = "UTF-16 BE (no BOM, detected from NUL pattern)", IsGuess = true, UnitSize = 2
                    };
                return BinaryResult("contains NUL bytes");
            }

            if (control * 50 > sample)
                return BinaryResult("high density of control characters");

            int multi, invalid;
            bool high;
            ScanUtf8(data, count, truncated, out multi, out invalid, out high);

            if (invalid == 0)
            {
                return new TextDetection
                {
                    Kind = TextKind.Text,
                    Encoding = new UTF8Encoding(false, false),
                    EncodingName = high ? "UTF-8 (no BOM)" : "ASCII (also valid UTF-8)",
                    UnitSize = 1
                };
            }

            if (multi >= invalid * 10)
            {
                return new TextDetection
                {
                    Kind = TextKind.Text,
                    Encoding = new UTF8Encoding(false, false),
                    EncodingName = "UTF-8 (no BOM)",
                    Note = invalid + " invalid byte sequence(s) shown as U+FFFD",
                    UnitSize = 1
                };
            }

            Encoding legacy = LegacyEncoding();
            return new TextDetection
            {
                Kind = TextKind.Text,
                Encoding = legacy,
                EncodingName = legacy.EncodingName + " (assumed legacy code page " + legacy.CodePage + ")",
                IsGuess = true,
                Note = "not valid UTF-8; legacy encoding is a guess",
                UnitSize = 1
            };
        }

        private static TextDetection Bom(Encoding enc, string name, int bomLength, int unit)
        {
            return new TextDetection
            {
                Kind = TextKind.Text, Encoding = enc, EncodingName = name, BomLength = bomLength, UnitSize = unit
            };
        }

        private static TextDetection BinaryResult(string reason)
        {
            return new TextDetection
            {
                Kind = TextKind.Binary, Encoding = null, EncodingName = null, Note = reason, UnitSize = 1
            };
        }

        private static Encoding LegacyEncoding()
        {
            try
            {
                int cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                if (cp <= 0 || cp >= 65000) cp = 1252;
                return Encoding.GetEncoding(cp);
            }
            catch
            {
                return Encoding.GetEncoding(28591);
            }
        }

        private static void ScanUtf8(byte[] d, int count, bool truncated, out int multi, out int invalid, out bool high)
        {
            multi = 0;
            invalid = 0;
            high = false;
            int i = 0;
            while (i < count)
            {
                byte b = d[i];
                if (b < 0x80) { i++; continue; }
                high = true;

                int need;
                byte lo = 0x80, hi = 0xBF;
                if (b >= 0xC2 && b <= 0xDF) need = 1;
                else if (b == 0xE0) { need = 2; lo = 0xA0; }
                else if (b == 0xED) { need = 2; hi = 0x9F; }
                else if (b >= 0xE1 && b <= 0xEF) need = 2;
                else if (b == 0xF0) { need = 3; lo = 0x90; }
                else if (b == 0xF4) { need = 3; hi = 0x8F; }
                else if (b >= 0xF1 && b <= 0xF3) need = 3;
                else { invalid++; i++; continue; }

                if (i + need >= count)
                {
                    if (truncated) break; // sequence cut by the read limit, not an error
                    invalid++;
                    i++;
                    continue;
                }

                bool ok = d[i + 1] >= lo && d[i + 1] <= hi;
                for (int k = 2; ok && k <= need; k++)
                    ok = d[i + k] >= 0x80 && d[i + k] <= 0xBF;

                if (ok) { multi++; i += need + 1; }
                else { invalid++; i++; }
            }
        }
    }

    internal static class TextStats
    {
        public static string DescribeLineEndings(string text)
        {
            int crlf = 0, lf = 0, cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                    else cr++;
                }
                else if (c == '\n')
                {
                    lf++;
                }
            }

            int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
            if (kinds == 0) return "no line breaks";
            if (kinds == 1) return crlf > 0 ? "CRLF" : (lf > 0 ? "LF" : "CR");
            return "mixed (CRLF " + crlf + ", LF " + lf + ", CR " + cr + ")";
        }

        public static int LongestLine(string text)
        {
            int longest = 0, current = 0;
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n')
                {
                    if (current > longest) longest = current;
                    current = 0;
                }
                else
                {
                    current++;
                }
            }
            return current > longest ? current : longest;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            string[] units = { "KiB", "MiB", "GiB", "TiB" };
            double v = bytes;
            int u = -1;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[u]
                + " (" + bytes.ToString("N0", CultureInfo.InvariantCulture) + " B)";
        }
    }

    // =====================================================================
    // Format catalog and signatures
    // =====================================================================

    internal enum FormatKind
    {
        PlainText,
        SourceCode,
        StructuredText,
        Binary
    }

    internal sealed class FormatInfo
    {
        public string Name { get; set; }
        public FormatKind Kind { get; set; }
        /// <summary>Highlighting language id (see LanguageSpecs) or null for none.</summary>
        public string LanguageId { get; set; }
    }

    /// <summary>Maps special filenames and extensions to formats. Add a Reg(...) line to support more.</summary>
    internal static class FormatCatalog
    {
        private static readonly Dictionary<string, FormatInfo> ByName =
            new Dictionary<string, FormatInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, FormatInfo> ByExtension =
            new Dictionary<string, FormatInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<FormatInfo> All = new List<FormatInfo>();

        /// <summary>Tokens starting with '.' are extensions (and dotfile names); others are exact filenames.</summary>
        private static void Reg(string name, FormatKind kind, string language, string keys)
        {
            var info = new FormatInfo { Name = name, Kind = kind, LanguageId = language };
            All.Add(info);
            foreach (string key in keys.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (key[0] == '.')
                {
                    ByExtension[key] = info;
                    ByName[key] = info;
                }
                else
                {
                    ByName[key] = info;
                }
            }
        }

        static FormatCatalog()
        {
            // Plain text and documentation
            Reg("Plain text", FormatKind.PlainText, null, ".txt .text .nfo .asc readme license licence copying authors notice changelog");
            Reg("reStructuredText", FormatKind.PlainText, null, ".rst");
            Reg("Log file", FormatKind.PlainText, "log", ".log");
            Reg("Markdown", FormatKind.StructuredText, "markdown", ".md .markdown .mdx");
            Reg("Delimited text (CSV/TSV)", FormatKind.StructuredText, null, ".csv .tsv .tab");
            Reg("Visual Studio solution", FormatKind.PlainText, null, ".sln");
            Reg("LaTeX", FormatKind.SourceCode, "tex", ".tex .sty .cls");
            Reg("BibTeX", FormatKind.StructuredText, "bib", ".bib");
            Reg("Diff / patch", FormatKind.StructuredText, "diff", ".diff .patch");

            // Programming languages
            Reg("Python", FormatKind.SourceCode, "python", ".py .pyw .pyi");
            Reg("C", FormatKind.SourceCode, "c", ".c");
            Reg("C/C++ header", FormatKind.SourceCode, "cpp", ".h");
            Reg("C++", FormatKind.SourceCode, "cpp", ".cc .cpp .cxx .c++ .hpp .hxx .hh .inl .ipp");
            Reg("C#", FormatKind.SourceCode, "csharp", ".cs .csx");
            Reg("Java", FormatKind.SourceCode, "java", ".java");
            Reg("JavaScript", FormatKind.SourceCode, "javascript", ".js .jsx .mjs .cjs");
            Reg("TypeScript", FormatKind.SourceCode, "typescript", ".ts .tsx .mts .cts");
            Reg("HTML", FormatKind.SourceCode, "html", ".html .htm .xhtml");
            Reg("CSS", FormatKind.SourceCode, "css", ".css");
            Reg("SCSS / Sass / Less", FormatKind.SourceCode, "scss", ".scss .sass .less");
            Reg("SQL", FormatKind.SourceCode, "sql", ".sql");
            Reg("Shell script", FormatKind.SourceCode, "shell", ".sh .bash .zsh .ksh .bashrc .zshrc .profile .bash_profile");
            Reg("PowerShell", FormatKind.SourceCode, "powershell", ".ps1 .psm1 .psd1");
            Reg("Batch file", FormatKind.SourceCode, "batch", ".bat .cmd");
            Reg("Rust", FormatKind.SourceCode, "rust", ".rs");
            Reg("Go", FormatKind.SourceCode, "go", ".go");
            Reg("PHP", FormatKind.SourceCode, "php", ".php .phtml");
            Reg("Ruby", FormatKind.SourceCode, "ruby", ".rb .rake .gemspec rakefile gemfile");
            Reg("Lua", FormatKind.SourceCode, "lua", ".lua");
            Reg("Perl", FormatKind.SourceCode, "perl", ".pl .pm .t");
            Reg("Swift", FormatKind.SourceCode, "swift", ".swift");
            Reg("Kotlin", FormatKind.SourceCode, "kotlin", ".kt .kts");
            Reg("Dart", FormatKind.SourceCode, "dart", ".dart");
            Reg("R", FormatKind.SourceCode, "r", ".r");
            Reg("MATLAB/Octave (.m is ambiguous with Objective-C)", FormatKind.SourceCode, "matlab", ".m");
            Reg("Assembly", FormatKind.SourceCode, "asm", ".asm .s .nasm");

            // Data and configuration
            Reg("JSON", FormatKind.StructuredText, "json", ".json .geojson .webmanifest .har");
            Reg("JSON with comments", FormatKind.StructuredText, "jsonc", ".jsonc .json5");
            Reg("Jupyter notebook (JSON, shown as text)", FormatKind.StructuredText, "json", ".ipynb");
            Reg("YAML", FormatKind.StructuredText, "yaml", ".yaml .yml");
            Reg("TOML", FormatKind.StructuredText, "toml", ".toml");
            Reg("INI / configuration", FormatKind.StructuredText, "ini",
                ".ini .cfg .conf .properties .env .editorconfig .desktop .inf .gitconfig .gitmodules .npmrc");
            Reg("XML", FormatKind.StructuredText, "xml",
                ".xml .xaml .xsd .xsl .xslt .svg .csproj .vbproj .fsproj .vcxproj .props .targets .resx .config .manifest .plist .rss .atom .wsdl .nuspec .pubxml .proj");
            Reg("Ignore / attributes file", FormatKind.StructuredText, "ignore", ".gitignore .dockerignore .npmignore .gitattributes .hgignore");
            Reg("Dockerfile", FormatKind.SourceCode, "dockerfile", "dockerfile containerfile .dockerfile");
            Reg("Makefile", FormatKind.SourceCode, "makefile", "makefile gnumakefile .mk .make");
            Reg("CMake", FormatKind.SourceCode, "cmake", "cmakelists.txt .cmake");
            Reg("HTTP request file", FormatKind.StructuredText, "http", ".http .rest");
            Reg("GraphQL", FormatKind.SourceCode, "graphql", ".graphql .gql");
            Reg("Protocol Buffers", FormatKind.SourceCode, "proto", ".proto");

            // Binary formats (never decoded as text)
            Reg("Raw binary data", FormatKind.Binary, null, ".bin .dat .dump .raw");
            Reg("Windows PE image", FormatKind.Binary, null, ".exe .dll .sys .ocx .scr .efi .cpl .drv");
            Reg("Object file", FormatKind.Binary, null, ".obj .o");
            Reg("Library", FormatKind.Binary, null, ".lib .a");
            Reg("Program database", FormatKind.Binary, null, ".pdb");
            Reg("Java class", FormatKind.Binary, null, ".class");
            Reg("Java archive (ZIP)", FormatKind.Binary, null, ".jar .war .ear");
            Reg("Python bytecode", FormatKind.Binary, null, ".pyc .pyo");
            Reg("ELF binary / shared object", FormatKind.Binary, null, ".elf .so");
            Reg("WebAssembly module", FormatKind.Binary, null, ".wasm");
            Reg("SQLite database", FormatKind.Binary, null, ".sqlite .sqlite3 .db .db3");
            Reg("ZIP archive", FormatKind.Binary, null, ".zip");
            Reg("ZIP-based container", FormatKind.Binary, null, ".docx .xlsx .pptx .odt .ods .nupkg .vsix .apk .msix");
            Reg("7-Zip archive", FormatKind.Binary, null, ".7z");
            Reg("RAR archive", FormatKind.Binary, null, ".rar");
            Reg("gzip file", FormatKind.Binary, null, ".gz .tgz");
            Reg("TAR archive", FormatKind.Binary, null, ".tar");
            Reg("PDF document", FormatKind.Binary, null, ".pdf");
        }

        /// <summary>Looks up by special filename first, then extension. Returns null if unknown.</summary>
        public static FormatInfo Lookup(string fileName, string extension)
        {
            string name = (fileName ?? string.Empty).ToLowerInvariant();
            FormatInfo info;
            if (name.Length > 0 && ByName.TryGetValue(name, out info)) return info;

            if ((name.StartsWith("dockerfile.") || name.StartsWith("containerfile.")) && ByName.TryGetValue("dockerfile", out info))
                return info;
            if (name.StartsWith(".env") && ByExtension.TryGetValue(".env", out info))
                return info;

            string ext = (extension ?? string.Empty).ToLowerInvariant();
            if (ext.Length > 0 && ByExtension.TryGetValue(ext, out info)) return info;
            return null;
        }

        public static IEnumerable<string> LanguageIds()
        {
            var seen = new HashSet<string>();
            foreach (FormatInfo f in All)
                if (f.LanguageId != null && seen.Add(f.LanguageId))
                    yield return f.LanguageId;
        }
    }

    /// <summary>Recognises a few file signatures so hex-only formats can be labelled honestly.</summary>
    internal static class Signatures
    {
        public static string Identify(byte[] d, int n)
        {
            if (StartsWith(d, n, Encoding.ASCII.GetBytes("%PDF-"))) return "PDF document";
            if (StartsWith(d, n, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return "7-Zip archive";
            if (StartsWith(d, n, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 })) return "RAR archive";
            if (StartsWith(d, n, new byte[] { 0x00, 0x61, 0x73, 0x6D })) return "WebAssembly module";
            if (StartsWith(d, n, Encoding.ASCII.GetBytes("!<arch>\n"))) return "Unix ar archive / static library";
            if (StartsWith(d, n, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return "OLE2 compound file";
            if (StartsWith(d, n, new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 })) return "XZ compressed data";
            if (StartsWith(d, n, new byte[] { 0x28, 0xB5, 0x2F, 0xFD })) return "Zstandard compressed data";
            if (StartsWith(d, n, Encoding.ASCII.GetBytes("BZh"))) return "bzip2 compressed data";
            if (n >= 262 && d[257] == 'u' && d[258] == 's' && d[259] == 't' && d[260] == 'a' && d[261] == 'r') return "TAR archive";
            if (n >= 4 && d[2] == 0x0D && d[3] == 0x0A) return "Python bytecode (probable)";
            return null;
        }

        private static bool StartsWith(byte[] d, int n, byte[] sig)
        {
            if (n < sig.Length) return false;
            for (int i = 0; i < sig.Length; i++)
                if (d[i] != sig[i]) return false;
            return true;
        }
    }

    // =====================================================================
    // Reader abstraction, registry and service
    // =====================================================================

    /// <summary>A strategy for reading one family of files.</summary>
    internal interface IPreviewReader
    {
        string Name { get; }
        ReaderCategory Category { get; }

        /// <summary>0 = cannot handle this file; the highest score wins (ties: first registered).</summary>
        int Score(FileProbe probe);

        PreviewResult Read(FileProbe probe, AppSettings settings, CancellationToken cancellation);
    }

    /// <summary>Holds the readers. To support a new format, implement IPreviewReader and Register it here.</summary>
    internal sealed class ReaderRegistry
    {
        private readonly List<IPreviewReader> readers = new List<IPreviewReader>();

        public IPreviewReader Fallback { get; private set; }

        public void Register(IPreviewReader reader)
        {
            readers.Add(reader);
        }

        public static ReaderRegistry CreateDefault()
        {
            var registry = new ReaderRegistry();
            registry.Register(new PeReader());
            registry.Register(new ZipReader());
            registry.Register(new ElfReader());
            registry.Register(new SqliteReader());
            registry.Register(new JavaClassReader());
            registry.Register(new GzipReader());
            registry.Register(new TextFileReader());
            var hex = new HexFileReader();
            registry.Register(hex);
            registry.Fallback = hex;
            return registry;
        }

        public IPreviewReader Select(FileProbe probe)
        {
            IPreviewReader best = Fallback;
            int bestScore = 0;
            foreach (IPreviewReader reader in readers)
            {
                int score;
                try { score = reader.Score(probe); }
                catch (Exception) { score = 0; }
                if (score > bestScore)
                {
                    best = reader;
                    bestScore = score;
                }
            }
            return best;
        }
    }

    /// <summary>Probes a file, selects a reader and returns a result. Never throws except for cancellation.</summary>
    internal sealed class PreviewService
    {
        private readonly ReaderRegistry registry;

        public PreviewService() : this(ReaderRegistry.CreateDefault()) { }

        public PreviewService(ReaderRegistry registry)
        {
            this.registry = registry;
        }

        public PreviewResult Load(string path, AppSettings settings, CancellationToken ct)
        {
            string full = path;
            try
            {
                try { full = Path.GetFullPath(path); } catch (Exception) { full = path; }

                if (Directory.Exists(full))
                    return PreviewResult.Failure(full, "This path is a directory, not a file: " + full);
                if (!File.Exists(full))
                    return PreviewResult.Failure(full, "File not found (it may have been moved or deleted):" + Environment.NewLine + full);

                ct.ThrowIfCancellationRequested();
                FileProbe probe = FileProbe.Open(full);
                IPreviewReader reader = registry.Select(probe);

                PreviewResult result;
                try
                {
                    result = reader.Read(probe, settings, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { throw; }
                catch (UnauthorizedAccessException) { throw; }
                catch (Exception ex)
                {
                    // A reader failed on malformed content: degrade to the raw-bytes view.
                    result = registry.Fallback.Read(probe, settings, ct);
                    string note = reader.Name + " could not parse this file (" + ex.Message + "); showing raw bytes instead.";
                    result.Notice = string.IsNullOrEmpty(result.Notice) ? note : note + " " + result.Notice;
                }

                result.FilePath = probe.FullPath;
                result.FileName = probe.FileName;
                result.Extension = probe.Extension;
                result.FileSize = probe.Length;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (FileNotFoundException)
            {
                return PreviewResult.Failure(full, "File not found (it may have been deleted while opening):" + Environment.NewLine + full);
            }
            catch (DirectoryNotFoundException)
            {
                return PreviewResult.Failure(full, "Folder not found:" + Environment.NewLine + full);
            }
            catch (UnauthorizedAccessException ex)
            {
                return PreviewResult.Failure(full, "Access denied: " + ex.Message + Environment.NewLine + full);
            }
            catch (IOException ex)
            {
                return PreviewResult.Failure(full, "I/O error: " + ex.Message + Environment.NewLine
                    + "The file may be locked, deleted or modified while being read." + Environment.NewLine + full);
            }
            catch (Exception ex)
            {
                return PreviewResult.Failure(full, "Unexpected error: " + ex.Message + Environment.NewLine + ex);
            }
        }
    }

    // =====================================================================
    // Raw-byte access and hex formatting
    // =====================================================================

    /// <summary>
    /// Read-only, block-cached random access to a file's raw bytes. Never loads the whole file.
    /// The file is opened with sharing so other processes may still write or delete it.
    /// </summary>
    internal sealed class HexDataSource : IDisposable
    {
        private const int PageSize = 65536;

        private readonly FileStream stream;
        private readonly byte[] page = new byte[PageSize];
        private long pageStart = -1;
        private int pageLength;

        public string Path { get; private set; }

        private HexDataSource(string path, FileStream stream)
        {
            Path = path;
            this.stream = stream;
        }

        public static HexDataSource Open(string path)
        {
            return new HexDataSource(path, StreamUtil.OpenShared(path, FileOptions.RandomAccess));
        }

        /// <summary>Current length (re-queried each time, so growth/truncation is noticed).</summary>
        public long Length { get { return stream.Length; } }

        /// <summary>Reads up to count bytes at offset. Returns the number actually read (short near EOF).</summary>
        public int Read(long offset, byte[] dest, int count)
        {
            if (offset < 0) return 0;
            int total = 0;
            while (total < count)
            {
                long off = offset + total;
                if (!EnsurePage(off)) break;
                int inPage = (int)(off - pageStart);
                int avail = pageLength - inPage;
                if (avail <= 0) break;
                int n = Math.Min(avail, count - total);
                Buffer.BlockCopy(page, inPage, dest, total, n);
                total += n;
            }
            return total;
        }

        private bool EnsurePage(long offset)
        {
            if (pageStart >= 0 && offset >= pageStart && offset < pageStart + pageLength) return true;
            long ps = offset - (offset % PageSize);
            stream.Seek(ps, SeekOrigin.Begin);
            int n = StreamUtil.ReadFully(stream, page, 0, PageSize);
            pageStart = ps;
            pageLength = n;
            return offset - ps < n;
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }

    internal static class HexFormatter
    {
        private const string Digits = "0123456789ABCDEF";

        public static int OffsetDigits(long length)
        {
            long max = Math.Max(0, length - 1);
            return max <= 0xFFFFFFFFL ? 8 : (max <= 0xFFFFFFFFFFFFL ? 12 : 16);
        }

        public static string FormatOffset(long offset, int digits)
        {
            return offset.ToString("X" + digits, CultureInfo.InvariantCulture);
        }

        /// <summary>Printable ASCII is shown as is; every other byte value is shown as a middle dot.</summary>
        public static char ToDisplayChar(byte b)
        {
            return (b >= 0x20 && b <= 0x7E) ? (char)b : '\u00B7';
        }

        /// <summary>Character index of byte i inside the hex column (3 chars per byte + 1 extra gap per 8 bytes).</summary>
        public static int HexColumn(int i)
        {
            return i * 3 + i / 8;
        }

        public static int HexWidthChars(int bytesPerRow)
        {
            return bytesPerRow * 3 + (bytesPerRow - 1) / 8;
        }

        /// <summary>Fixed-width hex column; missing bytes (last row) are blank.</summary>
        public static string FormatHexColumn(byte[] buf, int start, int n, int bytesPerRow)
        {
            var sb = new StringBuilder(HexWidthChars(bytesPerRow) + 1);
            for (int i = 0; i < bytesPerRow; i++)
            {
                if (i > 0 && i % 8 == 0) sb.Append(' ');
                if (i < n)
                {
                    byte b = buf[start + i];
                    sb.Append(Digits[b >> 4]).Append(Digits[b & 0xF]);
                }
                else
                {
                    sb.Append("  ");
                }
                sb.Append(' ');
            }
            return sb.ToString();
        }

        public static string FormatBytesHex(byte[] buf, int start, int count)
        {
            var sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                byte b = buf[start + i];
                sb.Append(Digits[b >> 4]).Append(Digits[b & 0xF]);
            }
            return sb.ToString();
        }

        public static string FormatAscii(byte[] buf, int start, int count)
        {
            var sb = new StringBuilder(count);
            for (int i = 0; i < count; i++) sb.Append(ToDisplayChar(buf[start + i]));
            return sb.ToString();
        }

        public static string FormatDumpLine(long offset, byte[] buf, int start, int count, int bytesPerRow, int offsetDigits)
        {
            return FormatOffset(offset, offsetDigits) + "  "
                + FormatHexColumn(buf, start, count, bytesPerRow) + " "
                + FormatAscii(buf, start, count);
        }
    }

    // =====================================================================
    // Readers: text and hex
    // =====================================================================

    /// <summary>Plain text, source code and structured text (all decoded; differs only by category/highlighting).</summary>
    internal sealed class TextFileReader : IPreviewReader
    {
        public string Name { get { return "Text reader"; } }
        public ReaderCategory Category { get { return ReaderCategory.PlainText; } }

        public int Score(FileProbe probe)
        {
            FormatInfo fmt = FormatCatalog.Lookup(probe.FileName, probe.Extension);
            if (fmt != null && fmt.Kind == FormatKind.Binary) return 0;
            if (probe.Detection.Kind == TextKind.Binary) return 0;
            return fmt != null ? 80 : 40;
        }

        public PreviewResult Read(FileProbe probe, AppSettings settings, CancellationToken ct)
        {
            FormatInfo fmt = FormatCatalog.Lookup(probe.FileName, probe.Extension);

            byte[] buf;
            int n;
            bool truncated;
            using (FileStream fs = StreamUtil.OpenShared(probe.FullPath, FileOptions.SequentialScan))
            {
                long len = fs.Length;
                int toRead = (int)Math.Min(len, settings.MaxTextBytes);
                buf = new byte[toRead];
                n = 0;
                while (n < toRead)
                {
                    ct.ThrowIfCancellationRequested();
                    int r = fs.Read(buf, n, Math.Min(1 << 20, toRead - n));
                    if (r <= 0) break;
                    n += r;
                }
                truncated = len > n;
            }

            TextDetection d = TextSniffer.Analyze(buf, n, truncated);
            if (d.Kind == TextKind.Binary)
                throw new InvalidDataException("The file content no longer looks like text.");

            string lang = fmt != null ? fmt.LanguageId : null;
            ReaderCategory cat;
            string readerName;
            string formatName;

            if (n == 0)
            {
                return new PreviewResult
                {
                    ReaderName = "Text reader",
                    Category = fmt == null ? ReaderCategory.Unknown : CategoryOf(fmt),
                    FormatName = fmt != null ? fmt.Name : "Empty file",
                    InitialView = ViewMode.Text,
                    Text = string.Empty,
                    IsEmpty = true,
                    EncodingName = d.EncodingName,
                    LineEndings = "no line breaks",
                    Notice = "Empty file (0 bytes)."
                };
            }

            int start = d.BomLength;
            int end = n;
            int unit = d.UnitSize;
            if (truncated) end = TrimToLine(buf, start, n, unit);
            end = start + ((end - start) / unit) * unit;

            Decoder decoder = d.Encoding.GetDecoder();
            int byteCount = end - start;
            char[] chars = new char[d.Encoding.GetMaxCharCount(byteCount)];
            int cn = decoder.GetChars(buf, start, byteCount, chars, 0, !truncated);
            string text = new string(chars, 0, cn);

            if (fmt == null) lang = SniffLanguage(text);

            if (fmt == null)
            {
                cat = lang != null ? ReaderCategory.SourceCode : ReaderCategory.Unknown;
                readerName = lang != null ? "Source code reader" : "Unknown-format text fallback";
                formatName = lang != null
                    ? LanguageSpecs.DisplayName(lang) + " (detected from content)"
                    : "Unknown format (content looks like text)";
            }
            else
            {
                cat = CategoryOf(fmt);
                readerName = cat == ReaderCategory.SourceCode ? "Source code reader"
                    : cat == ReaderCategory.StructuredText ? "Structured text reader" : "Plain text reader";
                formatName = fmt.Name;
            }

            bool disable = text.Length > 2000000 || TextStats.LongestLine(text) > 20000;
            var notices = new List<string>();
            if (truncated)
                notices.Add("Showing the first " + TextStats.FormatSize(end) + " of " + TextStats.FormatSize(probe.Length)
                    + " (cut at a line boundary).");
            if (d.Note != null) notices.Add(char.ToUpperInvariant(d.Note[0]) + d.Note.Substring(1) + ".");
            if (disable) notices.Add("Syntax highlighting disabled for performance (very large text or very long lines).");

            int fffd = 0;
            foreach (char c in text) if (c == '\uFFFD') fffd++;
            if (fffd > 0 && d.Note == null)
                notices.Add("Contains " + fffd + " U+FFFD replacement character(s) (invalid bytes or literal).");

            return new PreviewResult
            {
                ReaderName = readerName,
                Category = cat,
                FormatName = formatName,
                InitialView = ViewMode.Text,
                Text = text,
                LanguageId = disable ? null : lang,
                HighlightingDisabled = disable,
                EncodingName = d.EncodingName,
                EncodingIsGuess = d.IsGuess,
                LineEndings = TextStats.DescribeLineEndings(text),
                IsPartial = truncated,
                Notice = notices.Count > 0 ? string.Join(" ", notices) : null
            };
        }

        private static ReaderCategory CategoryOf(FormatInfo fmt)
        {
            switch (fmt.Kind)
            {
                case FormatKind.SourceCode: return ReaderCategory.SourceCode;
                case FormatKind.StructuredText: return ReaderCategory.StructuredText;
                default: return ReaderCategory.PlainText;
            }
        }

        private static int TrimToLine(byte[] buf, int start, int n, int unit)
        {
            if (unit != 1) return n;
            int lowest = Math.Max(start, n - 65536);
            for (int i = n - 1; i >= lowest; i--)
                if (buf[i] == 0x0A) return i + 1;
            return n;
        }

        /// <summary>Shebang / XML declaration sniffing, only used when the filename gives no language.</summary>
        private static string SniffLanguage(string text)
        {
            if (text.StartsWith("<?xml")) return "xml";
            if (!text.StartsWith("#!")) return null;
            int nl = text.IndexOfAny(new[] { '\r', '\n' });
            string line = (nl < 0 ? text : text.Substring(0, nl)).ToLowerInvariant();
            if (line.Contains("python")) return "python";
            if (line.Contains("pwsh") || line.Contains("powershell")) return "powershell";
            if (line.Contains("node") || line.Contains("deno")) return "javascript";
            if (line.Contains("perl")) return "perl";
            if (line.Contains("ruby")) return "ruby";
            if (line.Contains("lua")) return "lua";
            if (line.Contains("php")) return "php";
            if (line.Contains("bash") || line.Contains("/sh") || line.Contains(" sh") || line.Contains("zsh") || line.Contains("ksh"))
                return "shell";
            return null;
        }
    }

    /// <summary>Raw-bytes reader: fallback for unknown, binary and unparsed formats. Content is shown by the hex viewer.</summary>
    internal sealed class HexFileReader : IPreviewReader
    {
        public string Name { get { return "Hex reader"; } }
        public ReaderCategory Category { get { return ReaderCategory.Hex; } }

        public int Score(FileProbe probe)
        {
            return 10;
        }

        public PreviewResult Read(FileProbe probe, AppSettings settings, CancellationToken ct)
        {
            FormatInfo fmt = FormatCatalog.Lookup(probe.FileName, probe.Extension);
            string sig = Signatures.Identify(probe.Head, probe.HeadLength);
            string format = sig ?? (fmt != null ? fmt.Name : null);
            bool binary = probe.Detection.Kind == TextKind.Binary;
            bool known = fmt != null || sig != null;

            string notice;
            if (probe.Length == 0)
                notice = "Empty file (0 bytes).";
            else if (fmt != null && fmt.Kind != FormatKind.Binary && binary)
                notice = "Content of a text-type file looks binary (" + probe.Detection.Note + "); showing raw bytes.";
            else if (known)
                notice = "No structured reader for this format: hex view only.";
            else
                notice = "Unknown format; content looks binary. Showing raw bytes.";

            return new PreviewResult
            {
                ReaderName = "Hex reader",
                Category = known ? ReaderCategory.Hex : ReaderCategory.Unknown,
                FormatName = format ?? "Unknown binary data",
                InitialView = ViewMode.Hex,
                Text = null,
                IsEmpty = probe.Length == 0,
                ContentLooksBinary = binary,
                Notice = notice
            };
        }
    }

    // =====================================================================
    // Specialized binary readers (metadata only; nothing is executed or extracted)
    // =====================================================================

    internal static class Bin
    {
        public static byte[] ReadAt(Stream s, long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > s.Length)
                throw new InvalidDataException("Unexpected end of file at offset 0x" + offset.ToString("X") + ".");
            s.Seek(offset, SeekOrigin.Begin);
            byte[] b = new byte[count];
            if (StreamUtil.ReadFully(s, b, 0, count) != count)
                throw new InvalidDataException("Unexpected end of file at offset 0x" + offset.ToString("X") + ".");
            return b;
        }

        public static ushort U16(byte[] b, int o, bool le)
        {
            return le ? (ushort)(b[o] | (b[o + 1] << 8)) : (ushort)((b[o] << 8) | b[o + 1]);
        }

        public static uint U32(byte[] b, int o, bool le)
        {
            if (le) return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
            return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        }

        public static ulong U64(byte[] b, int o, bool le)
        {
            ulong a = U32(b, o, le), c = U32(b, o + 4, le);
            return le ? (a | (c << 32)) : ((a << 32) | c);
        }

        public static string AsciiZ(byte[] b, int o, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < max && o + i < b.Length && b[o + i] != 0; i++)
            {
                byte c = b[o + i];
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '?');
            }
            return sb.ToString();
        }

        /// <summary>Makes untrusted text safe to display: control and bidi-override characters become '?'.</summary>
        public static string Sanitize(string s, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length >= max) { sb.Append('\u2026'); break; }
                bool bad = c < 0x20 || c == 0x7F || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');
                sb.Append(bad ? '?' : c);
            }
            return sb.ToString();
        }

        public static void Line(StringBuilder sb, string label, string value)
        {
            sb.Append((label + ":").PadRight(24)).AppendLine(value);
        }

        public static string Hex(ulong v)
        {
            return "0x" + v.ToString("X");
        }
    }

    internal abstract class SpecializedBinaryReader : IPreviewReader
    {
        public abstract string FormatName { get; }
        public string Name { get { return FormatName + " reader"; } }
        public ReaderCategory Category { get { return ReaderCategory.SpecializedBinary; } }

        protected abstract bool Matches(FileProbe probe);
        protected abstract void Describe(FileStream fs, long length, StringBuilder sb);

        public int Score(FileProbe probe)
        {
            return Matches(probe) ? 100 : 0;
        }

        public PreviewResult Read(FileProbe probe, AppSettings settings, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.AppendLine(FormatName + " - metadata summary");
            sb.AppendLine("Read-only: the file is never executed, extracted or modified.");
            sb.AppendLine();
            Bin.Line(sb, "File size", TextStats.FormatSize(probe.Length));

            using (FileStream fs = StreamUtil.OpenShared(probe.FullPath, FileOptions.RandomAccess))
            {
                Describe(fs, fs.Length, sb);
            }

            return new PreviewResult
            {
                ReaderName = Name,
                Category = ReaderCategory.SpecializedBinary,
                FormatName = FormatName,
                InitialView = ViewMode.Text,
                Text = sb.ToString(),
                LanguageId = null,
                ContentLooksBinary = true,
                Notice = "Metadata summary. Press Ctrl+H or use the context menu for the hex view."
            };
        }
    }

    internal sealed class PeReader : SpecializedBinaryReader
    {
        public override string FormatName { get { return "Windows PE image"; } }

        protected override bool Matches(FileProbe p)
        {
            if (p.HeadLength < 0x40 || p.Head[0] != 'M' || p.Head[1] != 'Z') return false;
            uint lf = Bin.U32(p.Head, 0x3C, true);
            if (lf < 0x40 || lf + 4 > p.Length) return false;
            if (lf + 4 <= p.HeadLength)
                return p.Head[lf] == 'P' && p.Head[lf + 1] == 'E' && p.Head[lf + 2] == 0 && p.Head[lf + 3] == 0;
            return true;
        }

        protected override void Describe(FileStream fs, long length, StringBuilder sb)
        {
            byte[] dos = Bin.ReadAt(fs, 0, 64);
            long lf = Bin.U32(dos, 0x3C, true);
            byte[] sig = Bin.ReadAt(fs, lf, 4);
            if (sig[0] != 'P' || sig[1] != 'E' || sig[2] != 0 || sig[3] != 0)
                throw new InvalidDataException("MZ header without PE signature (DOS/16-bit executable?).");

            byte[] coff = Bin.ReadAt(fs, lf + 4, 20);
            ushort machine = Bin.U16(coff, 0, true);
            ushort nsec = Bin.U16(coff, 2, true);
            uint ts = Bin.U32(coff, 4, true);
            ushort optSize = Bin.U16(coff, 16, true);
            ushort chars = Bin.U16(coff, 18, true);

            Bin.Line(sb, "Machine", MachineName(machine) + " (0x" + machine.ToString("X4") + ")");
            Bin.Line(sb, "Type", (chars & 0x2000) != 0 ? "DLL" : ((chars & 0x0002) != 0 ? "Executable image" : "Image"));
            Bin.Line(sb, "Characteristics", "0x" + chars.ToString("X4") + ((chars & 0x0020) != 0 ? " (large address aware)" : ""));
            Bin.Line(sb, "Timestamp", ts == 0 ? "not set"
                : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToString("yyyy-MM-dd HH:mm:ss") + " UTC (0x"
                  + ts.ToString("X8") + "; may not be a real date for deterministic builds)");
            Bin.Line(sb, "Sections", nsec.ToString());

            int optRead = Math.Min((int)optSize, 240);
            byte[] opt = optRead >= 2 ? Bin.ReadAt(fs, lf + 24, optRead) : new byte[0];
            if (opt.Length >= 2)
            {
                ushort magic = Bin.U16(opt, 0, true);
                bool plus = magic == 0x20B;
                int need = plus ? 112 : 96;
                Bin.Line(sb, "Optional header", plus ? "PE32+ (64-bit)" : (magic == 0x10B ? "PE32 (32-bit)" : "unknown magic 0x" + magic.ToString("X4")));
                if (opt.Length >= need && (magic == 0x10B || plus))
                {
                    Bin.Line(sb, "Entry point (RVA)", Bin.Hex(Bin.U32(opt, 16, true)));
                    Bin.Line(sb, "Image base", Bin.Hex(plus ? Bin.U64(opt, 24, true) : Bin.U32(opt, 28, true)));
                    Bin.Line(sb, "Section alignment", Bin.Hex(Bin.U32(opt, 32, true)));
                    Bin.Line(sb, "File alignment", Bin.Hex(Bin.U32(opt, 36, true)));
                    Bin.Line(sb, "Size of image", Bin.Hex(Bin.U32(opt, 56, true)));
                    Bin.Line(sb, "Size of headers", Bin.Hex(Bin.U32(opt, 60, true)));
                    ushort sub = Bin.U16(opt, 68, true);
                    Bin.Line(sb, "Subsystem", SubsystemName(sub) + " (" + sub + ")");
                    Bin.Line(sb, "DLL characteristics", "0x" + Bin.U16(opt, 70, true).ToString("X4"));

                    uint dirs = Bin.U32(opt, plus ? 108 : 92, true);
                    int dirOff = need;
                    if (dirs > 14 && opt.Length >= dirOff + 15 * 8)
                        Bin.Line(sb, ".NET (CLR) header", Bin.U32(opt, dirOff + 14 * 8, true) != 0 ? "present (managed assembly)" : "absent (native)");
                    if (dirs > 4 && opt.Length >= dirOff + 5 * 8)
                        Bin.Line(sb, "Digital signature", Bin.U32(opt, dirOff + 4 * 8, true) != 0 ? "present (not validated)" : "absent");
                }
            }

            int n = Math.Min((int)nsec, 96);
            if (n > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Section table" + (nsec > n ? " (first " + n + " of " + nsec + ")" : "") + ":");
                sb.AppendLine("Name      VirtSize  VirtAddr  RawSize   RawPtr    Flags");
                try
                {
                    byte[] sec = Bin.ReadAt(fs, lf + 24 + optSize, n * 40);
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * 40;
                        uint fl = Bin.U32(sec, o + 36, true);
                        sb.AppendLine(Bin.AsciiZ(sec, o, 8).PadRight(10)
                            + Bin.U32(sec, o + 8, true).ToString("X8") + "  "
                            + Bin.U32(sec, o + 12, true).ToString("X8") + "  "
                            + Bin.U32(sec, o + 16, true).ToString("X8") + "  "
                            + Bin.U32(sec, o + 20, true).ToString("X8") + "  "
                            + ((fl & 0x40000000) != 0 ? "R" : "-") + ((fl & 0x80000000) != 0 ? "W" : "-")
                            + ((fl & 0x20000000) != 0 ? "X" : "-"));
                    }
                }
                catch (InvalidDataException)
                {
                    sb.AppendLine("(section table is truncated)");
                }
            }
        }

        private static string MachineName(ushort m)
        {
            switch (m)
            {
                case 0x14C: return "x86";
                case 0x8664: return "x64 (AMD64)";
                case 0x1C0: return "ARM";
                case 0x1C4: return "ARM Thumb-2";
                case 0xAA64: return "ARM64";
                case 0x200: return "IA-64";
                case 0x5064: return "RISC-V 64";
                default: return "unknown";
            }
        }

        private static string SubsystemName(ushort s)
        {
            switch (s)
            {
                case 1: return "Native";
                case 2: return "Windows GUI";
                case 3: return "Windows console";
                case 5: return "OS/2 console";
                case 7: return "POSIX console";
                case 9: return "Windows CE GUI";
                case 10: return "EFI application";
                case 11: return "EFI boot service driver";
                case 12: return "EFI runtime driver";
                case 13: return "EFI ROM";
                case 14: return "Xbox";
                case 16: return "Windows boot application";
                default: return "unknown";
            }
        }
    }

    internal sealed class ZipReader : SpecializedBinaryReader
    {
        private const int MaxEntries = 5000;
        private const int MaxDirectoryBytes = 16 * 1024 * 1024;

        public override string FormatName { get { return "ZIP archive"; } }

        protected override bool Matches(FileProbe p)
        {
            return p.HeadLength >= 4 && p.Head[0] == 0x50 && p.Head[1] == 0x4B
                && ((p.Head[2] == 3 && p.Head[3] == 4) || (p.Head[2] == 5 && p.Head[3] == 6));
        }

        protected override void Describe(FileStream fs, long length, StringBuilder sb)
        {
            int tailLen = (int)Math.Min(length, 65557);
            byte[] tail = Bin.ReadAt(fs, length - tailLen, tailLen);
            int eocd = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 5 && tail[i + 3] == 6) { eocd = i; break; }
            }
            if (eocd < 0) throw new InvalidDataException("End of central directory not found (truncated or not a ZIP).");

            ulong total = Bin.U16(tail, eocd + 10, true);
            ulong cdSize = Bin.U32(tail, eocd + 12, true);
            ulong cdOff = Bin.U32(tail, eocd + 16, true);
            int cmtLen = Bin.U16(tail, eocd + 20, true);
            bool zip64 = false;

            if (total == 0xFFFF || cdSize == 0xFFFFFFFF || cdOff == 0xFFFFFFFF)
            {
                int loc = eocd - 20;
                if (loc >= 0 && tail[loc] == 0x50 && tail[loc + 1] == 0x4B && tail[loc + 2] == 6 && tail[loc + 3] == 7)
                {
                    ulong z64 = Bin.U64(tail, loc + 8, true);
                    if (z64 < (ulong)length)
                    {
                        byte[] rec = Bin.ReadAt(fs, (long)z64, 56);
                        if (rec[0] == 0x50 && rec[1] == 0x4B && rec[2] == 6 && rec[3] == 6)
                        {
                            total = Bin.U64(rec, 32, true);
                            cdSize = Bin.U64(rec, 40, true);
                            cdOff = Bin.U64(rec, 48, true);
                            zip64 = true;
                        }
                    }
                }
            }

            Bin.Line(sb, "Entries", total.ToString() + (zip64 ? " (ZIP64)" : ""));
            Bin.Line(sb, "Central directory", "offset " + Bin.Hex(cdOff) + ", " + cdSize + " bytes");
            int cmtAvail = Math.Min(cmtLen, tail.Length - eocd - 22);
            if (cmtAvail > 0)
                Bin.Line(sb, "Archive comment", Bin.Sanitize(Encoding.GetEncoding(28591).GetString(tail, eocd + 22, cmtAvail), 200));

            if (cdOff + cdSize > (ulong)length) throw new InvalidDataException("Central directory lies beyond the end of the file.");
            int readLen = (int)Math.Min(cdSize, (ulong)MaxDirectoryBytes);
            byte[] cd = Bin.ReadAt(fs, (long)cdOff, readLen);

            Encoding cp437;
            try { cp437 = Encoding.GetEncoding(437); } catch { cp437 = Encoding.GetEncoding(28591); }

            sb.AppendLine();
            sb.AppendLine("Entry names come from the file and are untrusted; nothing is extracted.");
            sb.AppendLine("Size          Packed        Method       Modified          Name");

            int pos = 0, listed = 0;
            ulong sumUnc = 0, sumPacked = 0;
            while (pos + 46 <= cd.Length && (ulong)listed < total && listed < MaxEntries)
            {
                if (cd[pos] != 0x50 || cd[pos + 1] != 0x4B || cd[pos + 2] != 1 || cd[pos + 3] != 2) break;
                ushort flags = Bin.U16(cd, pos + 8, true);
                ushort method = Bin.U16(cd, pos + 10, true);
                ushort time = Bin.U16(cd, pos + 12, true);
                ushort date = Bin.U16(cd, pos + 14, true);
                ulong comp = Bin.U32(cd, pos + 20, true);
                ulong unc = Bin.U32(cd, pos + 24, true);
                int nameLen = Bin.U16(cd, pos + 28, true);
                int extraLen = Bin.U16(cd, pos + 30, true);
                int cmtL = Bin.U16(cd, pos + 32, true);
                if (pos + 46 + nameLen + extraLen + cmtL > cd.Length) break;

                string name = ((flags & 0x800) != 0 ? Encoding.UTF8 : cp437).GetString(cd, pos + 46, nameLen);

                int p = pos + 46 + nameLen, pe = p + extraLen;
                while (p + 4 <= pe)
                {
                    ushort id = Bin.U16(cd, p, true), sz = Bin.U16(cd, p + 2, true);
                    if (id == 1)
                    {
                        int q = p + 4;
                        if (unc == 0xFFFFFFFF && q + 8 <= pe) { unc = Bin.U64(cd, q, true); q += 8; }
                        if (comp == 0xFFFFFFFF && q + 8 <= pe) { comp = Bin.U64(cd, q, true); }
                    }
                    p += 4 + sz;
                }

                sumUnc += unc;
                sumPacked += comp;
                sb.AppendLine(unc.ToString().PadRight(14) + comp.ToString().PadRight(14)
                    + (MethodName(method) + ((flags & 1) != 0 ? "*" : "")).PadRight(13)
                    + DosDate(date, time).PadRight(18) + Bin.Sanitize(name, 260));

                listed++;
                pos += 46 + nameLen + extraLen + cmtL;
            }

            if (total > (ulong)listed)
                sb.AppendLine("... " + (total - (ulong)listed) + " more entries not listed (limit " + MaxEntries + " or directory unreadable).");
            sb.AppendLine();
            Bin.Line(sb, "Listed entries", listed + ", total size " + sumUnc + " B, packed " + sumPacked + " B");
            sb.AppendLine("(* = encrypted entry)");
        }

        private static string MethodName(ushort m)
        {
            switch (m)
            {
                case 0: return "Stored";
                case 8: return "Deflate";
                case 9: return "Deflate64";
                case 12: return "BZIP2";
                case 14: return "LZMA";
                case 93: return "Zstd";
                case 95: return "XZ";
                case 98: return "PPMd";
                case 99: return "AES";
                default: return "method " + m;
            }
        }

        private static string DosDate(ushort date, ushort time)
        {
            int y = ((date >> 9) & 0x7F) + 1980, mo = (date >> 5) & 0xF, d = date & 0x1F;
            int h = time >> 11, mi = (time >> 5) & 0x3F, s = (time & 0x1F) * 2;
            if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 59) return "invalid";
            try { return new DateTime(y, mo, d, h, mi, s).ToString("yyyy-MM-dd HH:mm"); }
            catch (ArgumentOutOfRangeException) { return "invalid"; }
        }
    }

    internal sealed class ElfReader : SpecializedBinaryReader
    {
        public override string FormatName { get { return "ELF binary"; } }

        protected override bool Matches(FileProbe p)
        {
            return p.HeadLength >= 20 && p.Head[0] == 0x7F && p.Head[1] == 'E' && p.Head[2] == 'L' && p.Head[3] == 'F';
        }

        protected override void Describe(FileStream fs, long length, StringBuilder sb)
        {
            byte[] h = Bin.ReadAt(fs, 0, (int)Math.Min(length, 64));
            bool is64 = h[4] == 2;
            bool le = h[5] != 2;
            if (h.Length < (is64 ? 64 : 52)) throw new InvalidDataException("ELF header is truncated.");

            Bin.Line(sb, "Class", is64 ? "64-bit" : (h[4] == 1 ? "32-bit" : "unknown"));
            Bin.Line(sb, "Byte order", le ? "little-endian" : "big-endian");
            Bin.Line(sb, "OS/ABI", OsAbi(h[7]));
            ushort type = Bin.U16(h, 16, le), machine = Bin.U16(h, 18, le);
            Bin.Line(sb, "Type", TypeName(type) + " (" + type + ")");
            Bin.Line(sb, "Machine", MachineName(machine) + " (0x" + machine.ToString("X") + ")");
            Bin.Line(sb, "Entry point", Bin.Hex(is64 ? Bin.U64(h, 24, le) : Bin.U32(h, 24, le)));
            if (is64)
            {
                Bin.Line(sb, "Program headers", Bin.U16(h, 56, le) + " at offset " + Bin.Hex(Bin.U64(h, 32, le)));
                Bin.Line(sb, "Section headers", Bin.U16(h, 60, le) + " at offset " + Bin.Hex(Bin.U64(h, 40, le)));
                Bin.Line(sb, "Flags", "0x" + Bin.U32(h, 48, le).ToString("X"));
            }
            else
            {
                Bin.Line(sb, "Program headers", Bin.U16(h, 44, le) + " at offset " + Bin.Hex(Bin.U32(h, 28, le)));
                Bin.Line(sb, "Section headers", Bin.U16(h, 48, le) + " at offset " + Bin.Hex(Bin.U32(h, 32, le)));
                Bin.Line(sb, "Flags", "0x" + Bin.U32(h, 36, le).ToString("X"));
            }
            sb.AppendLine();
            sb.AppendLine("Only the ELF header is decoded; section names and symbols are not parsed.");
        }

        private static string TypeName(ushort t)
        {
            switch (t)
            {
                case 1: return "Relocatable (object)";
                case 2: return "Executable";
                case 3: return "Shared object / PIE";
                case 4: return "Core dump";
                default: return "other";
            }
        }

        private static string OsAbi(byte b)
        {
            switch (b)
            {
                case 0: return "System V";
                case 2: return "NetBSD";
                case 3: return "Linux";
                case 6: return "Solaris";
                case 9: return "FreeBSD";
                case 12: return "OpenBSD";
                default: return "0x" + b.ToString("X2");
            }
        }

        private static string MachineName(ushort m)
        {
            switch (m)
            {
                case 3: return "x86";
                case 8: return "MIPS";
                case 0x14: return "PowerPC";
                case 0x15: return "PowerPC 64";
                case 0x16: return "S/390";
                case 0x28: return "ARM";
                case 0x2A: return "SuperH";
                case 0x3E: return "x86-64";
                case 0xB7: return "AArch64";
                case 0xF3: return "RISC-V";
                default: return "unknown";
            }
        }
    }

    internal sealed class SqliteReader : SpecializedBinaryReader
    {
        public override string FormatName { get { return "SQLite database"; } }

        protected override bool Matches(FileProbe p)
        {
            byte[] sig = Encoding.ASCII.GetBytes("SQLite format 3\0");
            if (p.HeadLength < sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (p.Head[i] != sig[i]) return false;
            return true;
        }

        protected override void Describe(FileStream fs, long length, StringBuilder sb)
        {
            byte[] h = Bin.ReadAt(fs, 0, 100);
            int page = Bin.U16(h, 16, false);
            if (page == 1) page = 65536;
            uint enc = Bin.U32(h, 56, false);
            uint ver = Bin.U32(h, 96, false);
            Bin.Line(sb, "Page size", page + " bytes");
            Bin.Line(sb, "Journal mode", h[18] == 2 || h[19] == 2 ? "WAL" : "rollback journal");
            Bin.Line(sb, "Reserved bytes/page", h[20].ToString());
            Bin.Line(sb, "Database size", Bin.U32(h, 28, false) + " pages (header value)");
            Bin.Line(sb, "File change counter", Bin.U32(h, 24, false).ToString());
            Bin.Line(sb, "Freelist pages", Bin.U32(h, 36, false).ToString());
            Bin.Line(sb, "Schema cookie", Bin.U32(h, 40, false).ToString());
            Bin.Line(sb, "Schema format", Bin.U32(h, 44, false).ToString());
            Bin.Line(sb, "Text encoding", enc == 1 ? "UTF-8" : enc == 2 ? "UTF-16 LE" : enc == 3 ? "UTF-16 BE" : "unknown (" + enc + ")");
            Bin.Line(sb, "user_version", Bin.U32(h, 60, false).ToString());
            Bin.Line(sb, "application_id", "0x" + Bin.U32(h, 68, false).ToString("X8"));
            Bin.Line(sb, "Last written by SQLite", (ver / 1000000) + "." + ((ver / 1000) % 1000) + "." + (ver % 1000));
            sb.AppendLine();
            sb.AppendLine("Header fields only. Tables and schemas are not read (no SQLite library is used,");
            sb.AppendLine("so the database is never opened by the SQLite engine).");
        }
    }

    internal sealed class JavaClassReader : SpecializedBinaryReader
    {
        public override string FormatName { get { return "Java class file"; } }

        protected override bool Matches(FileProbe p)
        {
            if (p.HeadLength < 10 || p.Head[0] != 0xCA || p.Head[1] != 0xFE || p.Head[2] != 0xBA || p.Head[3] != 0xBE) return false;
            int major = Bin.U16(p.Head, 6, false);
            return major >= 45 && major <= 120;
        }

        protected override void Describe(FileStream fs, long length, StringBuilder sb)
        {
            byte[] d = Bin.ReadAt(fs, 0, (int)Math.Min(length, 4 * 1024 * 1024));
            int minor = Bin.U16(d, 4, false), major = Bin.U16(d, 6, false), cpCount = Bin.U16(d, 8, false);
            string java = major >= 49 ? "Java " + (major - 44)
                : major == 45 ? "Java 1.1" : major == 46 ? "Java 1.2" : major == 47 ? "Java 1.3" : "Java 1.4";
            Bin.Line(sb, "Class file version", major + "." + minor + " (" + java + ")");
            Bin.Line(sb, "Constant pool entries", (cpCount - 1).ToString());

            string[] utf8 = new string[cpCount];
            int[] classNameIndex = new int[cpCount];
            int pos = 10;
            for (int i = 1; i < cpCount; i++)
            {
                Need(d, pos, 1);
                byte tag = d[pos++];
                switch (tag)
                {
                    case 1:
                        Need(d, pos, 2);
                        int len = Bin.U16(d, pos, false);
                        Need(d, pos + 2, len);
                        utf8[i] = Encoding.UTF8.GetString(d, pos + 2, len);
                        pos += 2 + len;
                        break;
                    case 3:
                    case 4: pos += 4; break;
                    case 5:
                    case 6: pos += 8; i++; break;
                    case 7:
                        Need(d, pos, 2);
                        classNameIndex[i] = Bin.U16(d, pos, false);
                        pos += 2;
                        break;
                    case 8:
                    case 16:
                    case 19:
                    case 20: pos += 2; break;
                    case 9:
                    case 10:
                    case 11:
                    case 12:
                    case 17:
                    case 18: pos += 4; break;
                    case 15: pos += 3; break;
                    default: throw new InvalidDataException("Unknown constant pool tag " + tag + ".");
                }
            }

            Need(d, pos, 8);
            int access = Bin.U16(d, pos, false), thisClass = Bin.U16(d, pos + 2, false), superClass = Bin.U16(d, pos + 4, false);
            int ifaces = Bin.U16(d, pos + 6, false);

            Func<int, string> className = idx =>
            {
                if (idx <= 0 || idx >= cpCount) return "(none)";
                int u = classNameIndex[idx];
                return u > 0 && u < cpCount && utf8[u] != null ? Bin.Sanitize(utf8[u].Replace('/', '.'), 300) : "(unresolved)";
            };

            Bin.Line(sb, "Class", className(thisClass));
            Bin.Line(sb, "Superclass", className(superClass));
            Bin.Line(sb, "Interfaces", ifaces.ToString());
            var flags = new List<string>();
            if ((access & 0x0001) != 0) flags.Add("public");
            if ((access & 0x0010) != 0) flags.Add("final");
            if ((access & 0x0200) != 0) flags.Add("interface");
            if ((access & 0x0400) != 0) flags.Add("abstract");
            if ((access & 0x1000) != 0) flags.Add("synthetic");
            if ((access & 0x2000) != 0) flags.Add("annotation");
            if ((access & 0x4000) != 0) flags.Add("enum");
            if ((access & 0x8000) != 0) flags.Add("module");
            Bin.Line(sb, "Access flags", "0x" + access.ToString("X4") + (flags.Count > 0 ? " (" + string.Join(", ", flags) + ")" : ""));
            sb.AppendLine();
            sb.AppendLine("Fields, methods and bytecode are not decoded.");
        }

        private static void Need(byte[] d, int pos, int count)
        {
            if (pos < 0 || count < 0 || pos + count > d.Length)
                throw new InvalidDataException("Class file is truncated or the constant pool is too large to read.");
        }
    }

    internal sealed class GzipReader : SpecializedBinaryReader
    {
        public override string FormatName { get { return "gzip file"; } }

        protected override bool Matches(FileProbe p)
        {
            return p.HeadLength >= 10 && p.Head[0] == 0x1F && p.Head[1] == 0x8B && p.Head[2] == 8;
        }

        protected override void Describe(FileStream fs, long length, StringBuilder sb)
        {
            byte[] h = Bin.ReadAt(fs, 0, (int)Math.Min(length, 4096));
            byte flags = h[3];
            uint mtime = Bin.U32(h, 4, true);
            Bin.Line(sb, "Method", "deflate");
            Bin.Line(sb, "Flags", "0x" + flags.ToString("X2"));
            Bin.Line(sb, "Modified", mtime == 0 ? "not set"
                : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mtime).ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            Bin.Line(sb, "Compression level", h[8] == 2 ? "maximum" : h[8] == 4 ? "fastest" : "default/unspecified");
            Bin.Line(sb, "Source OS", OsName(h[9]));

            int pos = 10;
            if ((flags & 4) != 0 && pos + 2 <= h.Length) pos += 2 + Bin.U16(h, pos, true);
            if ((flags & 8) != 0 && pos < h.Length)
            {
                Bin.Line(sb, "Original name", Bin.AsciiZ(h, pos, 1024));
                while (pos < h.Length && h[pos] != 0) pos++;
                pos++;
            }
            if ((flags & 16) != 0 && pos < h.Length)
                Bin.Line(sb, "Comment", Bin.AsciiZ(h, pos, 1024));

            if (length >= 18)
            {
                byte[] t = Bin.ReadAt(fs, length - 8, 8);
                Bin.Line(sb, "CRC-32 (trailer)", "0x" + Bin.U32(t, 0, true).ToString("X8"));
                Bin.Line(sb, "Uncompressed size", Bin.U32(t, 4, true) + " bytes (modulo 2^32; valid for a single member)");
            }
            sb.AppendLine();
            sb.AppendLine("The compressed data is not decompressed.");
        }

        private static string OsName(byte b)
        {
            switch (b)
            {
                case 0: return "FAT (MS-DOS/Windows)";
                case 3: return "Unix";
                case 7: return "Macintosh";
                case 10: return "NTFS (Windows)";
                case 11: return "Acorn RISCOS";
                case 255: return "unknown";
                default: return "code " + b;
            }
        }
    }
}