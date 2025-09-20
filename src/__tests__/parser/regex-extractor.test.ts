import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { RegexExtractor } from '../../extractors/regex-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('RegexExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Basic Regex Patterns and Character Classes', () => {
    it('should extract basic patterns, character classes, and quantifiers', async () => {
      const regexCode = `
// Basic literal matching
hello
world
test123

// Character classes
[abc]              // Match a, b, or c
[a-z]              // Match any lowercase letter
[A-Z]              // Match any uppercase letter
[0-9]              // Match any digit
[a-zA-Z0-9]        // Match alphanumeric characters
[^abc]             // Match anything except a, b, or c
[^0-9]             // Match non-digits

// Predefined character classes
\\d                 // Match any digit [0-9]
\\D                 // Match any non-digit [^0-9]
\\w                 // Match word characters [a-zA-Z0-9_]
\\W                 // Match non-word characters [^a-zA-Z0-9_]
\\s                 // Match whitespace characters
\\S                 // Match non-whitespace characters
\\.                 // Match any character (literal dot)
.                  // Match any character except newline

// Quantifiers
a?                 // Match 0 or 1 'a' (optional)
a*                 // Match 0 or more 'a'
a+                 // Match 1 or more 'a'
a{3}               // Match exactly 3 'a's
a{2,5}             // Match between 2 and 5 'a's
a{3,}              // Match 3 or more 'a's

// Lazy/non-greedy quantifiers
a*?                // Lazy zero or more
a+?                // Lazy one or more
a??                // Lazy optional
a{2,5}?            // Lazy range

// Anchors
^                  // Start of string/line
$                  // End of string/line
\\b                 // Word boundary
\\B                 // Non-word boundary
\\A                 // Start of string (absolute)
\\Z                 // End of string (absolute)

// Alternation
cat|dog|bird       // Match cat, dog, or bird
red|blue|green     // Match color names

// Escaping special characters
\\^                 // Literal caret
\\$                 // Literal dollar
\\.                 // Literal dot
\\*                 // Literal asterisk
\\+                 // Literal plus
\\?                 // Literal question mark
\\[                 // Literal opening bracket
\\]                 // Literal closing bracket
\\{                 // Literal opening brace
\\}                 // Literal closing brace
\\(                 // Literal opening parenthesis
\\)                 // Literal closing parenthesis
\\|                 // Literal pipe
\\\\                // Literal backslash

// Unicode categories (if supported)
\\p{L}              // Match any letter
\\p{N}              // Match any number
\\p{P}              // Match any punctuation
\\p{S}              // Match any symbol
\\P{L}              // Match any non-letter

// POSIX character classes
[:alnum:]          // Alphanumeric characters
[:alpha:]          // Alphabetic characters
[:digit:]          // Digit characters
[:lower:]          // Lowercase letters
[:upper:]          // Uppercase letters
[:space:]          // Whitespace characters
[:punct:]          // Punctuation characters
[:xdigit:]         // Hexadecimal digits

// Common patterns
\\d{3}-\\d{3}-\\d{4}                    // Phone number pattern
[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\\.[a-zA-Z]{2,}  // Email pattern
https?://[^\\s]+                       // URL pattern
#[0-9a-fA-F]{6}                       // Hex color code
\\b\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\b    // IP address pattern
\\$\\d+\\.\\d{2}                        // Currency pattern

// Complex character class combinations
[a-zA-Z0-9!@#$%^&*()_+-=\\[\\]{}|;':\",./<>?]  // Complex character set
[\\x00-\\x1F\\x7F]                     // Control characters
[\\u0000-\\u007F]                      // ASCII characters
[\\u0080-\\u00FF]                      // Latin-1 supplement

// Case-insensitive modifiers (notation varies by flavor)
(?i)test           // Case-insensitive test
(?-i)TEST          // Case-sensitive TEST
(?i:hello)         // Case-insensitive group

// Multiline and dotall modifiers
(?m)^line          // Multiline mode
(?s).              // Dotall mode (dot matches newline)
(?ms)              // Both multiline and dotall

// Free-spacing mode (ignore whitespace and allow comments)
(?x)
\\d{3}             # Three digits
-                 # Literal hyphen
\\d{3}             # Three more digits
-                 # Another hyphen
\\d{4}             # Four final digits

// Word boundaries with context
\\bword\\b          // Whole word "word"
\\bpre\\w+          // Words starting with "pre"
\\w+ing\\b          // Words ending with "ing"
\\b\\w{5}\\b         // Five-letter words
`;

      const result = await parserManager.parseString(regexCode, 'regex');
      const extractor = new RegexExtractor('regex', 'basic.regex', regexCode);
      const symbols = extractor.extractSymbols(result);


      // Basic literals
      const helloPattern = symbols.find(s => s.name === 'hello');
      expect(helloPattern).toBeDefined();
      expect(helloPattern?.kind).toBe(SymbolKind.Variable); // Regex patterns as variables

      const worldPattern = symbols.find(s => s.name === 'world');
      expect(worldPattern).toBeDefined();

      // Character classes
      const abcClass = symbols.find(s => s.name === '[abc]');
      expect(abcClass).toBeDefined();
      expect(abcClass?.kind).toBe(SymbolKind.Class); // Character classes as classes

      const alphaLowerClass = symbols.find(s => s.name === '[a-z]');
      expect(alphaLowerClass).toBeDefined();

      const alphaUpperClass = symbols.find(s => s.name === '[A-Z]');
      expect(alphaUpperClass).toBeDefined();

      const digitClass = symbols.find(s => s.name === '[0-9]');
      expect(digitClass).toBeDefined();

      const alphanumericClass = symbols.find(s => s.name === '[a-zA-Z0-9]');
      expect(alphanumericClass).toBeDefined();

      const negatedClass = symbols.find(s => s.name === '[^abc]');
      expect(negatedClass).toBeDefined();

      const nonDigitClass = symbols.find(s => s.name === '[^0-9]');
      expect(nonDigitClass).toBeDefined();

      // Predefined character classes
      const digitShorthand = symbols.find(s => s.name === '\\d');
      expect(digitShorthand).toBeDefined();
      expect(digitShorthand?.kind).toBe(SymbolKind.Constant); // Predefined classes as constants

      const nonDigitShorthand = symbols.find(s => s.name === '\\D');
      expect(nonDigitShorthand).toBeDefined();

      const wordShorthand = symbols.find(s => s.name === '\\w');
      expect(wordShorthand).toBeDefined();

      const nonWordShorthand = symbols.find(s => s.name === '\\W');
      expect(nonWordShorthand).toBeDefined();

      const whitespaceShorthand = symbols.find(s => s.name === '\\s');
      expect(whitespaceShorthand).toBeDefined();

      const nonWhitespaceShorthand = symbols.find(s => s.name === '\\S');
      expect(nonWhitespaceShorthand).toBeDefined();

      const anyCharacter = symbols.find(s => s.name === '.');
      expect(anyCharacter).toBeDefined();

      // Quantifiers
      const optionalA = symbols.find(s => s.name === 'a?');
      expect(optionalA).toBeDefined();
      expect(optionalA?.kind).toBe(SymbolKind.Function); // Quantified patterns as functions

      const zeroOrMoreA = symbols.find(s => s.name === 'a*');
      expect(zeroOrMoreA).toBeDefined();

      const oneOrMoreA = symbols.find(s => s.name === 'a+');
      expect(oneOrMoreA).toBeDefined();

      const exactlyThreeA = symbols.find(s => s.name === 'a{3}');
      expect(exactlyThreeA).toBeDefined();

      const rangeA = symbols.find(s => s.name === 'a{2,5}');
      expect(rangeA).toBeDefined();

      const minThreeA = symbols.find(s => s.name === 'a{3,}');
      expect(minThreeA).toBeDefined();

      // Lazy quantifiers
      const lazyZeroOrMore = symbols.find(s => s.name === 'a*?');
      expect(lazyZeroOrMore).toBeDefined();

      const lazyOneOrMore = symbols.find(s => s.name === 'a+?');
      expect(lazyOneOrMore).toBeDefined();

      const lazyOptional = symbols.find(s => s.name === 'a??');
      expect(lazyOptional).toBeDefined();

      const lazyRange = symbols.find(s => s.name === 'a{2,5}?');
      expect(lazyRange).toBeDefined();

      // Anchors
      const startAnchor = symbols.find(s => s.name === '^');
      expect(startAnchor).toBeDefined();
      expect(startAnchor?.kind).toBe(SymbolKind.Constant);

      const endAnchor = symbols.find(s => s.name === '$');
      expect(endAnchor).toBeDefined();

      const wordBoundary = symbols.find(s => s.name === '\\b');
      expect(wordBoundary).toBeDefined();

      const nonWordBoundary = symbols.find(s => s.name === '\\B');
      expect(nonWordBoundary).toBeDefined();

      // Alternation
      const animalAlternation = symbols.find(s => s.name === 'cat|dog|bird');
      expect(animalAlternation).toBeDefined();

      const colorAlternation = symbols.find(s => s.name === 'red|blue|green');
      expect(colorAlternation).toBeDefined();

      // Common patterns
      const phonePattern = symbols.find(s => s.name?.includes('\\d{3}-\\d{3}-\\d{4}'));
      expect(phonePattern).toBeDefined();

      const emailPattern = symbols.find(s => s.name?.includes('@') && s.name?.includes('[a-zA-Z0-9._%+-]+'));
      expect(emailPattern).toBeDefined();

      const urlPattern = symbols.find(s => s.name?.includes('https?://'));
      expect(urlPattern).toBeDefined();

      const hexColorPattern = symbols.find(s => s.name?.includes('#[0-9a-fA-F]{6}'));
      expect(hexColorPattern).toBeDefined();

      const ipPattern = symbols.find(s => s.name?.includes('\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}'));
      expect(ipPattern).toBeDefined();

      const currencyPattern = symbols.find(s => s.name?.includes('\\$\\d+\\.\\d{2}'));
      expect(currencyPattern).toBeDefined();

      // Unicode patterns
      const letterCategory = symbols.find(s => s.name === '\\p{L}');
      expect(letterCategory).toBeDefined();

      const numberCategory = symbols.find(s => s.name === '\\p{N}');
      expect(numberCategory).toBeDefined();

      // POSIX character classes
      const posixAlnum = symbols.find(s => s.name === '[:alnum:]');
      expect(posixAlnum).toBeDefined();

      const posixAlpha = symbols.find(s => s.name === '[:alpha:]');
      expect(posixAlpha).toBeDefined();

      // Modifiers
      const caseInsensitive = symbols.find(s => s.name?.includes('(?i)'));
      expect(caseInsensitive).toBeDefined();

      const multilineMode = symbols.find(s => s.name?.includes('(?m)'));
      expect(multilineMode).toBeDefined();

      const dotallMode = symbols.find(s => s.name?.includes('(?s)'));
      expect(dotallMode).toBeDefined();

      // Word boundary patterns
      const wholeWord = symbols.find(s => s.name === '\\bword\\b');
      expect(wholeWord).toBeDefined();

      const wordsStartingWithPre = symbols.find(s => s.name === '\\bpre\\w+');
      expect(wordsStartingWithPre).toBeDefined();

      const wordsEndingWithIng = symbols.find(s => s.name === '\\w+ing\\b');
      expect(wordsEndingWithIng).toBeDefined();
    });
  });

  describe('Advanced Regex Features and Groups', () => {
    it('should extract groups, backreferences, lookarounds, and advanced patterns', async () => {
      const regexCode = `
// Capturing groups
(abc)              // Basic capturing group
(\\d{4})            // Capture four digits
([a-zA-Z]+)        // Capture one or more letters
(\\w+)\\s+(\\w+)      // Capture two words separated by whitespace

// Named capturing groups
(?<year>\\d{4})                    // Named group 'year'
(?<month>\\d{2})                   // Named group 'month'
(?<day>\\d{2})                     // Named group 'day'
(?<email>[^@]+@[^@]+\\.[^@]+)       // Named group 'email'
(?P<username>\\w+)                  // Python-style named group
(?<word>\\b\\w+\\b)                 // Named group 'word'

// Non-capturing groups
(?:abc)            // Non-capturing group
(?:\\d+)            // Non-capturing digits
(?:cat|dog)        // Non-capturing alternation
(?:https?|ftp)://  // Non-capturing protocol group

// Backreferences
(\\w+)\\s+\\1        // Capture word, match same word again
(["\'])(.*?)\\1      // Match quoted string with same quote type
<(\\w+)>.*?</\\1>    // Match XML/HTML tags
(\\d{2})/(\\d{2})/(\\d{4})\\s+\\1/\\2/\\3  // Date repetition

// Named backreferences
(?<tag>\\w+).*?</\\k<tag>>         // Named backreference
(?<quote>["\']).*?\\k<quote>        // Match quoted content
(?P<open>\\().*?\\)(?P=open)       // Python-style named backreference

// Positive lookahead
(?=.*[a-z])        // Positive lookahead for lowercase
(?=.*[A-Z])        // Positive lookahead for uppercase
(?=.*\\d)           // Positive lookahead for digit
(?=.*[!@#$%])      // Positive lookahead for special char
password(?=.*\\d)   // Password with digit lookahead

// Negative lookahead
(?!.*password)     // Negative lookahead - no "password"
(?!\\d+$)           // Negative lookahead - not only digits
\\b(?!the\\b)\\w+    // Words not starting with "the"
(?![0-9])          // Not followed by digit

// Positive lookbehind
(?<=@)\\w+          // Word preceded by @
(?<=\\$)\\d+\\.\\d{2}  // Price amount after $
(?<=Mr\\.|Mrs\\.)\\s*\\w+  // Name after title
(?<=\\b\\w{3})ed    // "ed" after 3-letter word

// Negative lookbehind
(?<!\\d)\\d{4}      // 4 digits not preceded by digit
(?<!/)www\\.        // www. not preceded by /
\\b(?<!un)happy     // "happy" not preceded by "un"

// Conditional patterns
(a)?(?(1)b|c)      // If group 1 matches 'a', then 'b', else 'c'
(?<vowel>[aeiou])?(?(vowel)\\w*|\\d*)  // Named conditional

// Atomic groups (possessive quantifiers)
(?>\\d+)           // Atomic group for digits
a++               // Possessive one or more
a*+               // Possessive zero or more
a?+               // Possessive optional

// Complex email validation
(?:[a-z0-9!#$%&'*+/=?^_\`{|}~-]+(?:\\.[a-z0-9!#$%&'*+/=?^_\`{|}~-]+)*|"(?:[\\x01-\\x08\\x0b\\x0c\\x0e-\\x1f\\x21\\x23-\\x5b\\x5d-\\x7f]|\\\\[\\x01-\\x09\\x0b\\x0c\\x0e-\\x7f])*")@(?:(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?|\\[(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?|[a-z0-9-]*[a-z0-9]:(?:[\\x01-\\x08\\x0b\\x0c\\x0e-\\x1f\\x21-\\x5a\\x53-\\x7f]|\\\\[\\x01-\\x09\\x0b\\x0c\\x0e-\\x7f])+)\\])

// Complex URL validation
https?://(www\\.)?[-a-zA-Z0-9@:%._\\+~#=]{1,256}\\.[a-zA-Z0-9()]{1,6}\\b([-a-zA-Z0-9()@:%_\\+.~#?&//=]*)

// Password strength validation
^(?=.*[a-z])(?=.*[A-Z])(?=.*\\d)(?=.*[@$!%*?&])[A-Za-z\\d@$!%*?&]{8,}$

// Credit card validation
^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|3[47][0-9]{13}|3[0-9]{13}|6(?:011|5[0-9]{2})[0-9]{12})$

// Phone number variations
^(\\+1\\s?)?\\(?([0-9]{3})\\)?[\\s.-]?([0-9]{3})[\\s.-]?([0-9]{4})$

// Date patterns
^(0?[1-9]|1[0-2])/(0?[1-9]|[12]\\d|3[01])/(19|20)\\d{2}$  // MM/DD/YYYY
^(19|20)\\d{2}-(0[1-9]|1[0-2])-(0[1-9]|[12]\\d|3[01])$    // YYYY-MM-DD
^(0[1-9]|[12]\\d|3[01])\\.(0[1-9]|1[0-2])\\.(19|20)\\d{2}$  // DD.MM.YYYY

// Time patterns
^([01]?[0-9]|2[0-3]):[0-5][0-9]$                         // 24-hour time
^(1[0-2]|0?[1-9]):[0-5][0-9]\\s?(AM|PM)$                   // 12-hour time

// IPv4 and IPv6 patterns
^((25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\\.){3}(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$  // IPv4
^(([0-9a-fA-F]{1,4}:){7,7}[0-9a-fA-F]{1,4}|([0-9a-fA-F]{1,4}:){1,7}:|([0-9a-fA-F]{1,4}:){1,6}:[0-9a-fA-F]{1,4})$  // IPv6 (simplified)

// MAC address patterns
^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$                // MAC address

// File path patterns
^[a-zA-Z]:\\\\(?:[^\\\\/:*?"<>|\\r\\n]+\\\\)*[^\\\\/:*?"<>|\\r\\n]*$    // Windows path
^\\/(?:[^/\\0]+\\/)*[^/\\0]*$                                         // Unix path

// HTML tag matching
<(\\w+)(?:\\s+[^>]*)?>.*?</\\1>                            // Basic HTML tag
<(\\w+)(?:\\s+(?:\\w+(?:\\s*=\\s*(?:"[^"]*"|'[^']*'|[^\\s"'=<>\`]+))?\\s*)*)?>.*?</\\1>  // HTML with attributes

// JSON string validation
"(?:[^"\\\\]|\\\\.)*"                                      // JSON string

// CSS selector patterns
#[a-zA-Z][a-zA-Z0-9_-]*                                   // ID selector
\\.[a-zA-Z][a-zA-Z0-9_-]*                                  // Class selector
[a-zA-Z][a-zA-Z0-9]*                                      // Element selector

// Log parsing patterns
^(\\d{4}-\\d{2}-\\d{2})\\s+(\\d{2}:\\d{2}:\\d{2})\\s+\\[(\\w+)\\]\\s+(.*)$  // Log entry
^(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\\s+-\\s+-\\s+\\[(.*?)\\]\\s+"(.*?)"\\s+(\\d{3})\\s+(\\d+)$  // Apache access log

// SQL injection detection patterns
('|(\\-\\-)|(;)|(\\|)|(\\*)|(%))|(\`)|())|function\\s*\\(|script|javascript|vbscript
`;

      const result = await parserManager.parseString(regexCode, 'regex');
      const extractor = new RegexExtractor('regex', 'advanced.regex', regexCode);
      const symbols = extractor.extractSymbols(result);

      // Capturing groups
      const basicGroup = symbols.find(s => s.name === '(abc)');
      expect(basicGroup).toBeDefined();
      expect(basicGroup?.kind).toBe(SymbolKind.Class); // Groups as classes

      const digitGroup = symbols.find(s => s.name === '(\\d{4})');
      expect(digitGroup).toBeDefined();

      const letterGroup = symbols.find(s => s.name === '([a-zA-Z]+)');
      expect(letterGroup).toBeDefined();

      const twoWordGroup = symbols.find(s => s.name === '(\\w+)\\s+(\\w+)');
      expect(twoWordGroup).toBeDefined();

      // Named capturing groups
      const yearGroup = symbols.find(s => s.name?.includes('(?<year>\\d{4})'));
      expect(yearGroup).toBeDefined();

      const monthGroup = symbols.find(s => s.name?.includes('(?<month>\\d{2})'));
      expect(monthGroup).toBeDefined();

      const emailGroup = symbols.find(s => s.name?.includes('(?<email>'));
      expect(emailGroup).toBeDefined();

      const pythonStyleGroup = symbols.find(s => s.name?.includes('(?P<username>'));
      expect(pythonStyleGroup).toBeDefined();

      // Non-capturing groups
      const nonCapturingAbc = symbols.find(s => s.name === '(?:abc)');
      expect(nonCapturingAbc).toBeDefined();

      const nonCapturingDigits = symbols.find(s => s.name === '(?:\\d+)');
      expect(nonCapturingDigits).toBeDefined();

      const nonCapturingAlternation = symbols.find(s => s.name === '(?:cat|dog)');
      expect(nonCapturingAlternation).toBeDefined();

      // Backreferences
      const wordRepetition = symbols.find(s => s.name === '(\\w+)\\s+\\1');
      expect(wordRepetition).toBeDefined();

      const quotedString = symbols.find(s => s.name === '(["\'])(.*?)\\1');
      expect(quotedString).toBeDefined();

      const xmlTag = symbols.find(s => s.name === '<(\\w+)>.*?</\\1>');
      expect(xmlTag).toBeDefined();

      // Named backreferences
      const namedTagBackref = symbols.find(s => s.name?.includes('\\k<tag>'));
      expect(namedTagBackref).toBeDefined();

      const namedQuoteBackref = symbols.find(s => s.name?.includes('\\k<quote>'));
      expect(namedQuoteBackref).toBeDefined();

      // Positive lookahead
      const lowercaseLookahead = symbols.find(s => s.name === '(?=.*[a-z])');
      expect(lowercaseLookahead).toBeDefined();
      expect(lowercaseLookahead?.kind).toBe(SymbolKind.Method); // Lookarounds as methods

      const uppercaseLookahead = symbols.find(s => s.name === '(?=.*[A-Z])');
      expect(uppercaseLookahead).toBeDefined();

      const digitLookahead = symbols.find(s => s.name === '(?=.*\\d)');
      expect(digitLookahead).toBeDefined();

      const specialCharLookahead = symbols.find(s => s.name === '(?=.*[!@#$%])');
      expect(specialCharLookahead).toBeDefined();

      // Negative lookahead
      const noPasswordLookahead = symbols.find(s => s.name === '(?!.*password)');
      expect(noPasswordLookahead).toBeDefined();

      const notOnlyDigits = symbols.find(s => s.name === '(?!\\d+$)');
      expect(notOnlyDigits).toBeDefined();

      const notThe = symbols.find(s => s.name === '\\b(?!the\\b)\\w+');
      expect(notThe).toBeDefined();

      // Positive lookbehind
      const wordAfterAt = symbols.find(s => s.name === '(?<=@)\\w+');
      expect(wordAfterAt).toBeDefined();

      const priceAmount = symbols.find(s => s.name === '(?<=\\$)\\d+\\.\\d{2}');
      expect(priceAmount).toBeDefined();

      const nameAfterTitle = symbols.find(s => s.name?.includes('(?<=Mr\\.|Mrs\\.)'));
      expect(nameAfterTitle).toBeDefined();

      // Negative lookbehind
      const fourDigitsNotAfterDigit = symbols.find(s => s.name === '(?<!\\d)\\d{4}');
      expect(fourDigitsNotAfterDigit).toBeDefined();

      const wwwNotAfterSlash = symbols.find(s => s.name === '(?<!/)www\\.');
      expect(wwwNotAfterSlash).toBeDefined();

      const happyNotAfterUn = symbols.find(s => s.name === '\\b(?<!un)happy');
      expect(happyNotAfterUn).toBeDefined();

      // Conditional patterns
      const simpleConditional = symbols.find(s => s.name === '(a)?(?(1)b|c)');
      expect(simpleConditional).toBeDefined();

      const namedConditional = symbols.find(s => s.name?.includes('(?(vowel)'));
      expect(namedConditional).toBeDefined();

      // Atomic groups and possessive quantifiers
      const atomicGroup = symbols.find(s => s.name === '(?>\\d+)');
      expect(atomicGroup).toBeDefined();

      const possessiveOneOrMore = symbols.find(s => s.name === 'a++');
      expect(possessiveOneOrMore).toBeDefined();

      const possessiveZeroOrMore = symbols.find(s => s.name === 'a*+');
      expect(possessiveZeroOrMore).toBeDefined();

      const possessiveOptional = symbols.find(s => s.name === 'a?+');
      expect(possessiveOptional).toBeDefined();

      // Complex validation patterns
      const complexEmailValidation = symbols.find(s => s.name?.includes('(?:[a-z0-9!#$%&') && s.name?.length > 100);
      expect(complexEmailValidation).toBeDefined();

      const urlValidation = symbols.find(s => s.name?.includes('https?://(www\\.)?[-a-zA-Z0-9@:%._\\+~#=]'));
      expect(urlValidation).toBeDefined();

      const passwordStrength = symbols.find(s => s.name?.includes('^(?=.*[a-z])(?=.*[A-Z])(?=.*\\d)(?=.*[@$!%*?&])'));
      expect(passwordStrength).toBeDefined();

      const creditCardValidation = symbols.find(s => s.name?.includes('^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}'));
      expect(creditCardValidation).toBeDefined();

      // Phone number patterns
      const phoneNumberPattern = symbols.find(s => s.name?.includes('^(\\+1\\s?)?\\(?([0-9]{3})\\)?'));
      expect(phoneNumberPattern).toBeDefined();

      // Date patterns
      const mmddyyyyDate = symbols.find(s => s.name?.includes('^(0?[1-9]|1[0-2])/(0?[1-9]|[12]\\d|3[01])/(19|20)\\d{2}$'));
      expect(mmddyyyyDate).toBeDefined();

      const yyyymmddDate = symbols.find(s => s.name?.includes('^(19|20)\\d{2}-(0[1-9]|1[0-2])-(0[1-9]|[12]\\d|3[01])$'));
      expect(yyyymmddDate).toBeDefined();

      // Time patterns
      const time24Hour = symbols.find(s => s.name?.includes('^([01]?[0-9]|2[0-3]):[0-5][0-9]$'));
      expect(time24Hour).toBeDefined();

      const time12Hour = symbols.find(s => s.name?.includes('^(1[0-2]|0?[1-9]):[0-5][0-9]\\s?(AM|PM)$'));
      expect(time12Hour).toBeDefined();

      // Network patterns
      const ipv4Pattern = symbols.find(s => s.name?.includes('^((25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\\.){3}'));
      expect(ipv4Pattern).toBeDefined();

      const macAddressPattern = symbols.find(s => s.name?.includes('^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$'));
      expect(macAddressPattern).toBeDefined();

      // File path patterns
      const windowsPath = symbols.find(s => s.name?.includes('^[a-zA-Z]:\\\\(?:[^\\\\/:*?"<>|\\r\\n]+\\\\)*'));
      expect(windowsPath).toBeDefined();

      const unixPath = symbols.find(s => s.name?.includes('^\\/(?:[^/\\0]+\\/)*[^/\\0]*$'));
      expect(unixPath).toBeDefined();

      // HTML patterns
      const basicHtmlTag = symbols.find(s => s.name?.includes('<(\\w+)(?:\\s+[^>]*)?>.*?</\\1>'));
      expect(basicHtmlTag).toBeDefined();

      const htmlWithAttributes = symbols.find(s => s.name?.includes('<(\\w+)(?:\\s+(?:\\w+(?:\\s*=\\s*(?:"[^"]*"|'));
      expect(htmlWithAttributes).toBeDefined();

      // JSON and CSS patterns
      const jsonString = symbols.find(s => s.name?.includes('"(?:[^"\\\\]|\\\\.)*"'));
      expect(jsonString).toBeDefined();

      const idSelector = symbols.find(s => s.name?.includes('#[a-zA-Z][a-zA-Z0-9_-]*'));
      expect(idSelector).toBeDefined();

      const classSelector = symbols.find(s => s.name?.includes('\\.[a-zA-Z][a-zA-Z0-9_-]*'));
      expect(classSelector).toBeDefined();

      // Log parsing patterns
      const logEntry = symbols.find(s => s.name?.includes('^(\\d{4}-\\d{2}-\\d{2})\\s+(\\d{2}:\\d{2}:\\d{2})'));
      expect(logEntry).toBeDefined();

      const apacheLog = symbols.find(s => s.name?.includes('^(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\\s+-\\s+-\\s+\\['));
      expect(apacheLog).toBeDefined();

      // SQL injection detection
      const sqlInjectionPattern = symbols.find(s => s.name?.includes("('|(\\-\\-)|"));
      expect(sqlInjectionPattern).toBeDefined();
    });
  });

  describe('Regex Performance and Edge Cases', () => {
    it('should extract performance-critical patterns, edge cases, and optimization techniques', async () => {
      const regexCode = `
// Catastrophic backtracking examples (problematic patterns)
(a+)+b             // Exponential backtracking
(a|a)*b            // Inefficient alternation
(a+)+$             // Vulnerable to ReDoS attack
(x+x+)+y           // Nested quantifiers
([a-zA-Z]+)*       // Multiple greedy quantifiers

// Optimized alternatives
a+b                // Simple quantifier instead of (a+)+b
a*b                // Efficient alternation
a+$                // Direct quantifier
x+y                // Simplified pattern
[a-zA-Z]*          // Single quantifier

// Atomic groups to prevent backtracking
(?>a+)b            // Atomic group prevents backtracking
(?>\\d+)\\w         // Atomic group for digits
(?>\\w+)\\s         // Atomic group for words
(?>\\S+)\\s         // Atomic group for non-whitespace

// Possessive quantifiers (no backtracking)
\\d++               // Possessive one or more digits
\\w*+               // Possessive zero or more word chars
[a-z]++            // Possessive character class
.*+                // Possessive any character

// Anchored patterns for performance
^\\d{3}-\\d{3}-\\d{4}$    // Anchored phone number
^[a-zA-Z0-9]+$           // Anchored alphanumeric
^https?://              // Anchored URL start
\\.[a-zA-Z]{2,}$         // Anchored domain end

// Character class optimizations
[0-9]              // Explicit range
\\d                 // Shorthand (may be slower in some engines)
[a-zA-Z]           // Explicit ranges
\\w                 // Shorthand alternative
[\\r\\n]            // Explicit newline characters
\\s                 // Shorthand for whitespace

// Literal string optimizations
test               // Simple literal
\\Qtest.string\\E   // Quoted literal (some flavors)
test\\.string       // Escaped literal

// Unrolled loops for performance
(?:[^"\\\\]|\\\\.)*     // Unrolled loop for non-quote chars
(?:[^<]*<)*           // Unrolled loop for non-bracket chars
(?:[^\\r\\n]*[\\r\\n])* // Unrolled loop for lines

// Branch reset groups (some flavors)
(?|(\\d{2})/(\\d{2})/(\\d{4})|(\\d{4})-(\\d{2})-(\\d{2}))  // Branch reset for date formats

// Recursive patterns (PCRE)
\\(([^()]|(?R))*\\)                    // Balanced parentheses
<([^<>]|(?R))*>                      // Balanced angle brackets
\\{([^{}]|(?R))*\\}                    // Balanced curly braces

// Subroutine calls (PCRE)
(\\d{3})-(?1)-\\d{4}                   // Call group 1
(?<area>\\d{3})-(?&area)-\\d{4}        // Named subroutine call

// Comment syntax
(?# This is a comment in the regex)
# This is a comment in free-spacing mode

// Free-spacing mode examples
(?x)
^                  # Start of string
\\d{3}              # Three digits
-                 # Literal hyphen
\\d{3}              # Three more digits
-                 # Another hyphen
\\d{4}              # Four final digits
$                  # End of string

// Unicode property blocks
\\p{InBasicLatin}          // Basic Latin block
\\p{InLatin-1Supplement}   // Latin-1 Supplement
\\p{InLatinExtended-A}     // Latin Extended-A
\\p{InLatinExtended-B}     // Latin Extended-B
\\p{InCyrillic}            // Cyrillic block
\\p{InGreek}               // Greek block
\\p{InHebrew}              // Hebrew block
\\p{InArabic}              // Arabic block
\\p{InCJKUnifiedIdeographs} // CJK Unified Ideographs

// Unicode categories
\\p{Ll}                    // Lowercase letter
\\p{Lu}                    // Uppercase letter
\\p{Lt}                    // Titlecase letter
\\p{Lm}                    // Modifier letter
\\p{Lo}                    // Other letter
\\p{Mn}                    // Non-spacing mark
\\p{Mc}                    // Spacing combining mark
\\p{Me}                    // Enclosing mark
\\p{Nd}                    // Decimal digit number
\\p{Nl}                    // Letter number
\\p{No}                    // Other number
\\p{Pc}                    // Connector punctuation
\\p{Pd}                    // Dash punctuation
\\p{Ps}                    // Open punctuation
\\p{Pe}                    // Close punctuation
\\p{Pi}                    // Initial punctuation
\\p{Pf}                    // Final punctuation
\\p{Po}                    // Other punctuation
\\p{Sm}                    // Math symbol
\\p{Sc}                    // Currency symbol
\\p{Sk}                    // Modifier symbol
\\p{So}                    // Other symbol
\\p{Zs}                    // Space separator
\\p{Zl}                    // Line separator
\\p{Zp}                    // Paragraph separator
\\p{Cc}                    // Control character
\\p{Cf}                    // Format character
\\p{Cs}                    // Surrogate
\\p{Co}                    // Private use
\\p{Cn}                    // Unassigned

// Control characters and special sequences
\\a                        // Bell (alarm)
\\e                        // Escape
\\f                        // Form feed
\\n                        // Newline
\\r                        // Carriage return
\\t                        // Tab
\\v                        // Vertical tab
\\0                        // Null character
\\x20                      // Hexadecimal character code
\\x{1F600}                 // Unicode character by hex code
\\c[                       // Control character
\\N{GREEK CAPITAL LETTER ALPHA}  // Unicode character by name

// Regex engine differences and compatibility
(*LIMIT_MATCH=1000)        // PCRE directive
(*LIMIT_RECURSION=100)     // PCRE recursion limit
(*NO_AUTO_POSSESS)         // Disable auto-possessification
(*UTF8)                    // UTF-8 mode
(*UCP)                     // Unicode character properties

// Conditional compilation patterns
(*MARK:label)              // Set mark for backtrack control
(*PRUNE)                   // Prune backtrack
(*SKIP)                    // Skip to next position
(*THEN)                    // Force backtrack
(*COMMIT)                  // Commit match
(*FAIL)                    // Force failure
(*ACCEPT)                  // Accept match

// Regex assembly (hypothetical optimization)
start:                     // Label
  match 'a'               // Literal match
  repeat 1,5              // Quantifier
  capture start           // Capture group
  lookahead 'b'           // Lookahead assertion
  end                     // End pattern

// Error handling patterns
(?C42)                     // Callout for debugging
(?C"debug message")        // Named callout
\\Q...\\E                   // Literal text block
(*VERB:name)               // Custom verbs

// Memory and performance considerations
[^\\r\\n]{0,1000}           // Bounded repetition
.{1,100}?                 // Lazy bounded quantifier
(?>[^"]*"[^"]*")*         // Atomic group for efficiency
\\G\\w+\\s*                // Contiguous matching

// Real-world optimization examples
^(?=.*[a-z])(?=.*[A-Z])(?=.*\\d)[a-zA-Z\\d@$!%*?&]{8,}$  // Optimized password
^[a-zA-Z0-9.!#$%&'*+/=?^_\`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$  // RFC-compliant email

// Anti-patterns to avoid
.*.*               // Redundant quantifiers
(.*)*              // Catastrophic backtracking
.+?.*              // Conflicting quantifiers
(a|b|c|d|e|f|g|h|i|j|k|l|m|n|o|p|q|r|s|t|u|v|w|x|y|z)  // Long alternation (use [a-z] instead)
`;

      const result = await parserManager.parseString(regexCode, 'regex');
      const extractor = new RegexExtractor('regex', 'performance.regex', regexCode);
      const symbols = extractor.extractSymbols(result);


      // Catastrophic backtracking patterns
      const exponentialBacktrack = symbols.find(s => s.name === '(a+)+b');
      expect(exponentialBacktrack).toBeDefined();
      expect(exponentialBacktrack?.kind).toBe(SymbolKind.Variable);

      const inefficientAlternation = symbols.find(s => s.name === '(a|a)*b');
      expect(inefficientAlternation).toBeDefined();

      const redosVulnerable = symbols.find(s => s.name === '(a+)+$');
      expect(redosVulnerable).toBeDefined();

      const nestedQuantifiers = symbols.find(s => s.name === '(x+x+)+y');
      expect(nestedQuantifiers).toBeDefined();

      // Optimized alternatives
      const optimizedSimple = symbols.find(s => s.name === 'a+b');
      expect(optimizedSimple).toBeDefined();

      const efficientAlternation = symbols.find(s => s.name === 'a*b');
      expect(efficientAlternation).toBeDefined();

      const directQuantifier = symbols.find(s => s.name === 'a+$');
      expect(directQuantifier).toBeDefined();

      // Atomic groups
      const atomicGroupA = symbols.find(s => s.name === '(?>a+)b');
      expect(atomicGroupA).toBeDefined();

      const atomicDigits = symbols.find(s => s.name === '(?>\\d+)\\w');
      expect(atomicDigits).toBeDefined();

      const atomicWords = symbols.find(s => s.name === '(?>\\w+)\\s');
      expect(atomicWords).toBeDefined();

      // Possessive quantifiers
      const possessiveDigits = symbols.find(s => s.name === '\\d++');
      expect(possessiveDigits).toBeDefined();

      const possessiveWords = symbols.find(s => s.name === '\\w*+');
      expect(possessiveWords).toBeDefined();

      const possessiveCharClass = symbols.find(s => s.name === '[a-z]++');
      expect(possessiveCharClass).toBeDefined();

      const possessiveAnyChar = symbols.find(s => s.name === '.*+');
      expect(possessiveAnyChar).toBeDefined();

      // Anchored patterns
      const anchoredPhone = symbols.find(s => s.name === '^\\d{3}-\\d{3}-\\d{4}$');
      expect(anchoredPhone).toBeDefined();

      const anchoredAlphanumeric = symbols.find(s => s.name === '^[a-zA-Z0-9]+$');
      expect(anchoredAlphanumeric).toBeDefined();

      const anchoredUrlStart = symbols.find(s => s.name === '^https?://');
      expect(anchoredUrlStart).toBeDefined();

      const anchoredDomainEnd = symbols.find(s => s.name === '\\.[a-zA-Z]{2,}$');
      expect(anchoredDomainEnd).toBeDefined();

      // Character class optimizations
      const explicitDigitRange = symbols.find(s => s.name === '[0-9]');
      expect(explicitDigitRange).toBeDefined();

      const explicitLetterRange = symbols.find(s => s.name === '[a-zA-Z]');
      expect(explicitLetterRange).toBeDefined();

      const explicitNewlines = symbols.find(s => s.name === '[\\r\\n]');
      expect(explicitNewlines).toBeDefined();

      // Literal optimizations
      const simpleLiteral = symbols.find(s => s.name === 'test');
      expect(simpleLiteral).toBeDefined();

      const quotedLiteral = symbols.find(s => s.name === '\\Qtest.string\\E');
      expect(quotedLiteral).toBeDefined();

      const escapedLiteral = symbols.find(s => s.name === 'test\\.string');
      expect(escapedLiteral).toBeDefined();

      // Unrolled loops
      const unrolledQuotes = symbols.find(s => s.name === '(?:[^"\\\\]|\\\\.)*');
      expect(unrolledQuotes).toBeDefined();

      const unrolledBrackets = symbols.find(s => s.name === '(?:[^<]*<)*');
      expect(unrolledBrackets).toBeDefined();

      const unrolledLines = symbols.find(s => s.name === '(?:[^\\r\\n]*[\\r\\n])*');
      expect(unrolledLines).toBeDefined();

      // Recursive patterns
      const balancedParens = symbols.find(s => s.name?.includes('\\(([^()]|(?R))*\\)'));
      expect(balancedParens).toBeDefined();

      const balancedAngles = symbols.find(s => s.name?.includes('<([^<>]|(?R))*>'));
      expect(balancedAngles).toBeDefined();

      const balancedBraces = symbols.find(s => s.name?.includes('\\{([^{}]|(?R))*\\}'));
      expect(balancedBraces).toBeDefined();

      // Subroutine calls
      const subroutineCall = symbols.find(s => s.name?.includes('(\\d{3})-(?1)-\\d{4}'));
      expect(subroutineCall).toBeDefined();

      const namedSubroutine = symbols.find(s => s.name?.includes('(?<area>\\d{3})-(?&area)-\\d{4}'));
      expect(namedSubroutine).toBeDefined();

      // Unicode property blocks
      const basicLatinBlock = symbols.find(s => s.name === '\\p{InBasicLatin}');
      expect(basicLatinBlock).toBeDefined();

      const cyrillicBlock = symbols.find(s => s.name === '\\p{InCyrillic}');
      expect(cyrillicBlock).toBeDefined();

      const cjkBlock = symbols.find(s => s.name === '\\p{InCJKUnifiedIdeographs}');
      expect(cjkBlock).toBeDefined();

      // Unicode categories
      const lowercaseLetter = symbols.find(s => s.name === '\\p{Ll}');
      expect(lowercaseLetter).toBeDefined();

      const uppercaseLetter = symbols.find(s => s.name === '\\p{Lu}');
      expect(uppercaseLetter).toBeDefined();

      const decimalDigit = symbols.find(s => s.name === '\\p{Nd}');
      expect(decimalDigit).toBeDefined();

      const mathSymbol = symbols.find(s => s.name === '\\p{Sm}');
      expect(mathSymbol).toBeDefined();

      const currencySymbol = symbols.find(s => s.name === '\\p{Sc}');
      expect(currencySymbol).toBeDefined();

      const spaceSeparator = symbols.find(s => s.name === '\\p{Zs}');
      expect(spaceSeparator).toBeDefined();

      const controlCharacter = symbols.find(s => s.name === '\\p{Cc}');
      expect(controlCharacter).toBeDefined();

      // Control characters and special sequences
      const bellChar = symbols.find(s => s.name === '\\a');
      expect(bellChar).toBeDefined();

      const escapeChar = symbols.find(s => s.name === '\\e');
      expect(escapeChar).toBeDefined();

      const formFeed = symbols.find(s => s.name === '\\f');
      expect(formFeed).toBeDefined();

      const newlineChar = symbols.find(s => s.name === '\\n');
      expect(newlineChar).toBeDefined();

      const carriageReturn = symbols.find(s => s.name === '\\r');
      expect(carriageReturn).toBeDefined();

      const tabChar = symbols.find(s => s.name === '\\t');
      expect(tabChar).toBeDefined();

      const verticalTab = symbols.find(s => s.name === '\\v');
      expect(verticalTab).toBeDefined();

      const nullChar = symbols.find(s => s.name === '\\0');
      expect(nullChar).toBeDefined();

      const hexChar = symbols.find(s => s.name === '\\x20');
      expect(hexChar).toBeDefined();

      const unicodeHex = symbols.find(s => s.name === '\\x{1F600}');
      expect(unicodeHex).toBeDefined();

      const controlChar = symbols.find(s => s.name === '\\c[');
      expect(controlChar).toBeDefined();

      const unicodeByName = symbols.find(s => s.name === '\\N{GREEK CAPITAL LETTER ALPHA}');
      expect(unicodeByName).toBeDefined();

      // PCRE directives
      const limitMatch = symbols.find(s => s.name?.includes('(*LIMIT_MATCH=1000)'));
      expect(limitMatch).toBeDefined();

      const limitRecursion = symbols.find(s => s.name?.includes('(*LIMIT_RECURSION=100)'));
      expect(limitRecursion).toBeDefined();

      const utf8Mode = symbols.find(s => s.name?.includes('(*UTF8)'));
      expect(utf8Mode).toBeDefined();

      const ucpMode = symbols.find(s => s.name?.includes('(*UCP)'));
      expect(ucpMode).toBeDefined();

      // Backtrack control
      const markLabel = symbols.find(s => s.name?.includes('(*MARK:label)'));
      expect(markLabel).toBeDefined();

      const pruneBacktrack = symbols.find(s => s.name?.includes('(*PRUNE)'));
      expect(pruneBacktrack).toBeDefined();

      const skipPosition = symbols.find(s => s.name?.includes('(*SKIP)'));
      expect(skipPosition).toBeDefined();

      const forceBacktrack = symbols.find(s => s.name?.includes('(*THEN)'));
      expect(forceBacktrack).toBeDefined();

      const commitMatch = symbols.find(s => s.name?.includes('(*COMMIT)'));
      expect(commitMatch).toBeDefined();

      const forceFail = symbols.find(s => s.name?.includes('(*FAIL)'));
      expect(forceFail).toBeDefined();

      const acceptMatch = symbols.find(s => s.name?.includes('(*ACCEPT)'));
      expect(acceptMatch).toBeDefined();

      // Callouts and debugging
      const numericCallout = symbols.find(s => s.name?.includes('(?C42)'));
      expect(numericCallout).toBeDefined();

      const namedCallout = symbols.find(s => s.name?.includes('(?C"debug message")'));
      expect(namedCallout).toBeDefined();

      // Performance considerations
      const boundedRepetition = symbols.find(s => s.name === '[^\\r\\n]{0,1000}');
      expect(boundedRepetition).toBeDefined();

      const lazyBounded = symbols.find(s => s.name === '.{1,100}?');
      expect(lazyBounded).toBeDefined();

      const atomicEfficiency = symbols.find(s => s.name?.includes('(?>[^"]*"[^"]*")*'));
      expect(atomicEfficiency).toBeDefined();

      const contiguousMatching = symbols.find(s => s.name === '\\G\\w+\\s*');
      expect(contiguousMatching).toBeDefined();

      // Real-world optimizations
      const optimizedPassword = symbols.find(s => s.name?.includes('^(?=.*[a-z])(?=.*[A-Z])(?=.*\\d)[a-zA-Z\\d@$!%*?&]{8,}$'));
      expect(optimizedPassword).toBeDefined();

      const rfcEmail = symbols.find(s => s.name?.includes('^[a-zA-Z0-9.!#$%&') && s.name?.length > 200);
      expect(rfcEmail).toBeDefined();

      // Anti-patterns
      const redundantQuantifiers = symbols.find(s => s.name === '.*.*');
      expect(redundantQuantifiers).toBeDefined();

      const catastrophicPattern = symbols.find(s => s.name === '(.*)*');
      expect(catastrophicPattern).toBeDefined();

      const conflictingQuantifiers = symbols.find(s => s.name === '.+?.*');
      expect(conflictingQuantifiers).toBeDefined();

      const longAlternation = symbols.find(s => s.name?.includes('(a|b|c|d|e|f|g|h|i|j|k|l|m|n|o|p|q|r|s|t|u|v|w|x|y|z)'));
      expect(longAlternation).toBeDefined();
    });
  });
});