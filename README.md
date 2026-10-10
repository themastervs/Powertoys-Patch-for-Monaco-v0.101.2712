# PowerToys Patch for Monaco Preview Handler — v0.101.2712

Replace the PowerToys Monaco preview executable with a custom file viewer, bringing support for additional text formats, structured data, and binary file inspection to Windows File Explorer.
*By default powertoys just uses Monaco in some files, that is limited by the app itself. If you want to use this patch with files which are not included you shall change de .reg shellex* (version 3)

## Overview

This project provides an alternative to the Monaco Preview Handler shipped with Microsoft PowerToys **v0.101.2712**.

The replacement uses a custom WPF-based viewer built with C# and AvalonEdit. Instead of relying on Monaco and its WebView2-based rendering infrastructure, it provides a native desktop interface for inspecting files directly from File Explorer's preview pane.

The project was initially developed to work around problems encountered with the original Monaco preview handler and to provide greater control over file-format support.

## Features

* **Custom preview interface:** A WPF-based viewer using AvalonEdit for text editing and syntax highlighting capabilities.
* **Extended format support:** Handles source code, configuration files, documentation, structured data, and other text formats.
* **Binary inspection:** Specialized readers for selected binary formats, with hexadecimal viewing as a fallback.
* **Executable metadata inspection:** Extracts information from supported PE files without executing them.
* **On-demand hexadecimal viewing:** Reads binary data in blocks rather than requiring the entire file to be loaded into memory.
* **Encoding detection:** Supports BOM-based detection, UTF-16 detection based on NUL-byte patterns, UTF-8 validation, and fallback to the system ANSI code page.
* **Extensible architecture:** Designed to allow additional file formats and readers to be added over time.

## Supported formats

The viewer classifies files into three main categories.

### 1. Text and source code

Examples include:

* Python, C, C++, C#, Java, JavaScript, TypeScript, Rust, Go, PHP, Ruby, Lua, Perl, Swift, Kotlin and Dart.
* HTML, CSS, SCSS, Sass and Less.
* PowerShell, shell scripts and Windows batch files.
* JSON, JSONC, JSON5, YAML and TOML.
* XML and related formats, including XAML, SVG and project files.
* INI files, environment files, Git configuration, Dockerfiles and Makefiles.
* Markdown, reStructuredText, CSV, TSV, SQL, LaTeX, BibTeX, logs and diff files.

Syntax highlighting is lexical rather than a complete language parser. Advanced constructs and embedded languages may not be highlighted perfectly.

### 2. Binary formats with specialized readers

The viewer includes readers for selected formats:

| Format                                 | Information displayed                                                                         |
| -------------------------------------- | --------------------------------------------------------------------------------------------- |
| PE (Windows executables and libraries) | Machine type, sections, subsystem, timestamp, .NET metadata and digital-signature information |
| ZIP                                    | Entry names, sizes, dates, compression methods and ZIP64 information                          |
| ELF                                    | Architecture, endianness, ABI, entry point and section information                            |
| SQLite                                 | Header metadata, page size, encoding and version information                                  |
| Java class files                       | Class version, superclass, interfaces and flags                                               |
| gzip                                   | Header flags, timestamp, compression metadata and available size information                  |

These readers inspect file structures and metadata. They do not execute files or extract archive contents.

### 3. Hexadecimal fallback

Files without a specialized reader can be inspected as hexadecimal data.

Examples include:

* Raw binary, `.bin`, `.dat`, `.dump` and `.raw`.
* Object files, libraries and PDB files.
* Python bytecode.
* ELF shared objects and WebAssembly files.
* 7-Zip, RAR and TAR archives.
* SQLite and ZIP files whose signatures do not match the expected format.

The viewer also includes signature-based detection for selected formats, allowing some files to be recognized even when their extensions are misleading.

**PDF is intentionally excluded from the supported-format list in this patch configuration.** Office documents and other unsupported container formats are not rendered as full documents; they may instead be inspected through their underlying binary structure.

## File detection and encoding

When a file extension is not recognized, the viewer can attempt to identify text from its contents.

Detection includes:

* Shebangs for supported scripting languages.
* XML declarations.
* Text-versus-binary heuristics based on NUL bytes and control-character density.

Text encoding detection follows a heuristic sequence involving BOMs, UTF-16 patterns, UTF-8 validation and the system ANSI code page.

Detection is necessarily heuristic: unusual encodings and malformed files may not be interpreted correctly.

## Requirements

* Windows 11.
* Microsoft PowerToys **v0.101.2712** for the version-specific patch.
* The files and dependencies included or required by the selected project version.

Compatibility with other PowerToys releases has not been established by this README.

## Installation

**Back up the original PowerToys files and registry configuration before applying any patch.**

1. Download or clone this repository.
2. Review the available version directories and select the appropriate patch for your PowerToys installation.
3. Read the instructions and inspect the files included with that version.
4. Apply the replacement according to the instructions provided for that version.
5. Open File Explorer and test the preview pane with several supported file types.

The replacement targets the Monaco preview component used by PowerToys. File associations and the way Windows invokes preview handlers may also affect whether a particular extension is previewed.

Do not assume that a patch for one PowerToys version will work with another.

## Limitations

* Syntax highlighting is lexical and may not handle every language construct.
* JSX and TSX do not receive full embedded JavaScript-in-HTML parsing.
* SQLite inspection focuses on header metadata rather than displaying database tables.
* PE inspection does not provide a complete analysis of imported and exported symbols.
* PDF, images, audio and video are not rendered as full documents or media.
* Binary signature detection and encoding detection may produce imperfect results for malformed or ambiguous files.
* File extensions associated with the preview handler may require additional Windows registry configuration.

## Safety

The viewer is intended for inspection, not execution.

Specialized binary readers inspect metadata and structures; archive contents are not extracted. Nevertheless, file parsing is not risk-free. Use caution with untrusted or malformed files, and keep backups of any system components modified during installation.

## Project structure

The repository contains multiple version directories:

* `version1`
* `version2`
* `version3`

Consult the contents of each directory to determine the implementation, installation procedure and differences between versions.

## Disclaimer

This is an unofficial community project. It is not affiliated with, endorsed by, or supported by Microsoft or the PowerToys team.

PowerToys and its associated components remain the property of their respective owners. This project is intended to provide an alternative preview implementation for a specific PowerToys release.

## Contributing

Bug reports, reproducible compatibility issues and suggestions for additional file formats are welcome. When reporting an issue, include your Windows version, PowerToys version, affected file extension and relevant error messages.
