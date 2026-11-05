# Security

Iso20022Kit reads payment messages that may come from anyone, so every part of it treats its input as hostile: the
.NET library, the `iso20022` command and the browser viewer.

## Reporting a problem

Please report vulnerabilities privately through GitHub's **Security → Report a vulnerability** on this repository,
not in a public issue. Include an input that shows the problem if you can (made-up data only).

## What is protected, and how

| Threat | Protection | Tested by |
|---|---|---|
| **XML external entities (XXE), entity expansion ("billion laughs")** | DTDs are refused before anything is read (`XML-DTD`); only the five predefined entities exist. | conformance `security-*`, hostile `entity-*` |
| **Fetching from the network or disk** | `xsi:schemaLocation` and `noNamespaceSchemaLocation` are ignored; the schemas are embedded. The viewer's Content-Security-Policy has `connect-src 'none'`, so the page can't send a message anywhere. | `SchemaLocationsAreNeverFetched` (a listener checks nothing connects), e2e "every hostile file" (every request is same-origin) |
| **Validation bypass** | The Document's namespace must be exactly the message's (a trailing space once made .NET check against nothing and report "valid"); a Document that the schema didn't assess is an error; an input with two Documents is refused (`MSG-MULTIPLE-DOCUMENTS`); `xsi:type` and `xsi:nil` can't change what is checked. | hostile `namespace-*`, `two-documents`, `xsi-*` |
| **The browser and .NET disagreeing** | The viewer's XML reader follows the same rules as .NET's: allowed characters, end tags matching exactly, namespace declarations, comments, the XML declaration, XML whitespace (a no-break space isn't), line endings, attribute normalisation. | every hostile case runs in both test suites with the same expectations |
| **Denial of service** | Size limit (50 million characters), nesting limit (256 levels), issue limit (1000), and no quadratic work: paths, newline tracking and document search are linear. | `DeepNestingIsRefusedQuickly`, `AMillionSiblingsFinish`, `ThousandsOfErrorsStopAtTheLimit` and their viewer equivalents, with time limits |
| **Crashes on bad input** | `Validate` never throws for input: amounts that overflow `decimal`, short IBANs and garbage values with `AlwaysRunRules`, characters beyond Unicode in references. In the viewer, names such as `__proto__` or `constructor` are ordinary keys (lookup tables have no prototype). | `NeverThrowsEvenWithRulesForcedOnBrokenMessages` (every conformance and hostile file), hostile `char-*`, `element-proto`, `prefix-*` |
| **Misread bytes** | Streams and files are decoded by their byte-order mark or XML declaration, and invalid bytes are an error, never silently replaced. | hostile `utf8-invalid-bytes`, `latin1-declared`, `utf16le-bom` |
| **Display spoofing** | Invisible and direction-changing characters (right-to-left overrides, zero-width spaces, C1 controls) get a `HIDDEN-CHARACTERS` warning; in issue messages they appear as `‮`; the viewer shows them as visible markers and isolates every value's direction. | hostile `bidi-*`, `zero-width-*`, `c1-control`; e2e "a right-to-left override in a name is shown as a marker" |
| **Huge or hostile text in reports** | Quoted values are shortened to 600 characters and escaped, so a 2 MB value isn't repeated, and terminal escape characters can't reach a terminal. The `read` command prints JSON, which escapes them too. | `LongValuesAreNotRepeatedInFull`, `IssueMessagesNeverContainHiddenCharacters` |
| **Script injection in the viewer** | The page never turns message text into markup: all text goes in with `textContent`. The policy also enforces Trusted Types, so even a future mistake (`innerHTML` with message data) would be blocked by the browser. Scripts, styles and images load only from the page's own origin. | hostile `markup-in-text`; e2e "script-looking text stays text", "Trusted Types block HTML injection sinks" |
| **Clickjacking** | `frame-ancestors 'none'` and `X-Frame-Options: DENY`. | e2e "the page can't be framed by another site" |
| **The `iso20022` command** | Files are read as streams with a size check; a `conformance` suite can only point at files in its own folder. | manual checks; see the CLI section in the README |
| **Hostile MT messages** | The MT reader is limited to 20,000 characters and 200 fields, and brace nesting in headers to two levels. Tags must be two digits and an optional capital letter. Patterns are anchored with `\z`, so a trailing line break can't slip past `$`. Every regex has a time limit. Errors quote input escaped and shortened. `Read` and the translators never throw for bad input. | `MtTests`: broken and huge messages, 10,000 mutated messages (which found three bugs: a NUL passing the character-set check, an amount with a trailing line break, raw tags in messages) |
| **Field injection when writing MT** | Every value from a pacs.008 is put on one line and converted to the X character set before it is wrapped. A line that would start with `:` (a new field) or `-` (the end of the text block) gets a leading `.`. | `AnMxNameCantInjectAnotherMtField`, `ALineStartingWithAColonOrDashIsDefused` |
| **Supply chain** | Four npm development dependencies (esbuild, TypeScript, Node types, Playwright) at exact versions with a lockfile; no runtime dependencies in the viewer or the library. CI runs `npm audit` and `dotnet list package --vulnerable`, and pins GitHub Actions to commits. | CI |

Four of these were also checked the other way round: with the namespace check, the hidden-character markers, Trusted Types or the anti-framing headers removed, their tests fail.

## What it doesn't do

- It checks that a message is well-formed and consistent. It doesn't authenticate it: anyone can write a valid pacs.008. Signatures, MAC checks and sanctions screening belong to the systems that send and receive payments.
- Scheme rules (CBPR+, SEPA, FedNow…) aren't checked; see [docs/rules.md](docs/rules.md).
- The viewer runs entirely in the browser and stores nothing. A message pasted into it stays in that tab.
