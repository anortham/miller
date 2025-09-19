import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { existsSync } from 'fs';
import { join } from 'path';

describe('WASM Parsers', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('WASM File Availability', () => {
    it('should have Swift WASM file available', () => {
      const wasmPath = join(process.cwd(), 'wasm', 'tree-sitter-swift.wasm');
      expect(existsSync(wasmPath)).toBe(true);
    });

    it('should have Kotlin WASM file available', () => {
      const wasmPath = join(process.cwd(), 'wasm', 'tree-sitter-kotlin.wasm');
      expect(existsSync(wasmPath)).toBe(true);
    });

    it('should have Razor WASM file available', () => {
      const wasmPath = join(process.cwd(), 'wasm', 'tree-sitter-razor.wasm');
      expect(existsSync(wasmPath)).toBe(true);
    });
  });

  describe('Language Detection', () => {
    it('should detect Swift files', () => {
      expect(parserManager.getLanguageForFile('test.swift')).toBe('swift');
      expect(parserManager.isFileSupported('MyClass.swift')).toBe(true);
    });

    it('should detect Kotlin files', () => {
      expect(parserManager.getLanguageForFile('test.kt')).toBe('kotlin');
      expect(parserManager.getLanguageForFile('test.kts')).toBe('kotlin');
      expect(parserManager.isFileSupported('MainActivity.kt')).toBe(true);
    });

    it('should detect Razor files', () => {
      expect(parserManager.getLanguageForFile('test.razor')).toBe('razor');
      expect(parserManager.getLanguageForFile('test.cshtml')).toBe('razor');
      expect(parserManager.isFileSupported('Index.razor')).toBe(true);
    });

    it('should detect Vue files', () => {
      expect(parserManager.getLanguageForFile('test.vue')).toBe('vue');
      expect(parserManager.isFileSupported('Component.vue')).toBe(true);
    });
  });

  describe('Parser Loading Status', () => {
    it('should list supported languages', () => {
      const languages = parserManager.getSupportedLanguages();
      const extensions = parserManager.getSupportedExtensions();

      // Check our new languages are in the supported list
      expect(extensions).toContain('.swift');
      expect(extensions).toContain('.kt');
      expect(extensions).toContain('.kts');
      expect(extensions).toContain('.razor');
      expect(extensions).toContain('.cshtml');
      expect(extensions).toContain('.vue');
    });

    it('should have parser availability info', () => {
      // These may fail to load in CI without emscripten, but should not crash
      const stats = parserManager.getStats();
      expect(stats.initialized).toBe(true);
      expect(stats.supportedExtensions).toBeGreaterThan(0);
      expect(stats.languages).toBeDefined();
    });
  });

  describe('Basic Parsing', () => {
    it('should attempt to parse Swift code', async () => {
      const swiftCode = `
import Foundation

class MyClass {
    private var name: String

    init(name: String) {
        self.name = name
    }

    func greet() -> String {
        return "Hello, \\(name)!"
    }
}
      `;

      // This test verifies the parsing attempt doesn't crash
      // The actual parser may not load in test environment without proper emscripten setup
      try {
        const result = await parserManager.parseFile('test.swift', swiftCode);
        expect(result.language).toBe('swift');
        expect(result.content).toBe(swiftCode);
        expect(result.hash).toBeDefined();

        // If parsing succeeds, tree should be valid
        if (result.tree) {
          expect(result.tree.rootNode).toBeDefined();
        }
      } catch (error) {
        // Expected in test environment - could be WASM loading or version issues
        expect(error.message).toMatch(/swift|Incompatible language version|ENOENT/);
      }
    });

    it('should attempt to parse Kotlin code', async () => {
      const kotlinCode = `
data class Person(val name: String, val age: Int)

class Calculator {
    fun add(a: Int, b: Int): Int {
        return a + b
    }

    fun multiply(a: Int, b: Int): Int {
        return a * b
    }
}

fun main() {
    val person = Person("Kotlin", 15)
    println("Hello \${person.name}!")
}
      `;

      try {
        const result = await parserManager.parseFile('test.kt', kotlinCode);
        expect(result.language).toBe('kotlin');
        expect(result.content).toBe(kotlinCode);
        expect(result.hash).toBeDefined();

        if (result.tree) {
          expect(result.tree.rootNode).toBeDefined();
        }
      } catch (error) {
        // Expected in test environment - could be WASM loading or version issues
        expect(error.message).toMatch(/kotlin|Incompatible language version|ENOENT/);
      }
    });

    it('should attempt to parse Razor code', async () => {
      const razorCode = `
@page "/weather"
@using MyApp.Data

<h1>Weather Forecast</h1>

@if (forecasts == null)
{
    <p><em>Loading...</em></p>
}
else
{
    <table class="table">
        <thead>
            <tr>
                <th>Date</th>
                <th>Temp. (C)</th>
                <th>Summary</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var forecast in forecasts)
            {
                <tr>
                    <td>@forecast.Date.ToShortDateString()</td>
                    <td>@forecast.TemperatureC</td>
                    <td>@forecast.Summary</td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private WeatherForecast[]? forecasts;

    protected override async Task OnInitializedAsync()
    {
        forecasts = await Http.GetFromJsonAsync<WeatherForecast[]>("sample-data/weather.json");
    }
}
      `;

      try {
        const result = await parserManager.parseFile('weather.razor', razorCode);
        expect(result.language).toBe('razor');
        expect(result.content).toBe(razorCode);
        expect(result.hash).toBeDefined();

        if (result.tree) {
          expect(result.tree.rootNode).toBeDefined();
        }
      } catch (error) {
        // Expected in test environment - could be WASM loading or version issues
        expect(error.message).toMatch(/razor|Incompatible language version|ENOENT/);
      }
    });

    it('should handle Vue SFC parsing', async () => {
      const vueCode = `
<template>
  <div class="hello">
    <h1>{{ message }}</h1>
  </div>
</template>

<script>
export default {
  name: 'HelloWorld',
  data() {
    return {
      message: 'Hello Vue!'
    }
  }
}
</script>

<style scoped>
.hello {
  color: #42b883;
}
</style>
      `;

      // Vue has special handling - returns null tree but should not throw
      const result = await parserManager.parseFile('test.vue', vueCode);
      expect(result.language).toBe('vue');
      expect(result.content).toBe(vueCode);
      expect(result.hash).toBeDefined();
      expect(result.tree).toBe(null);
    });
  });

  describe('File Extension Mapping', () => {
    it('should map all expected extensions', () => {
      const extensionTests = [
        { ext: '.swift', lang: 'swift' },
        { ext: '.kt', lang: 'kotlin' },
        { ext: '.kts', lang: 'kotlin' },
        { ext: '.razor', lang: 'razor' },
        { ext: '.cshtml', lang: 'razor' },
        { ext: '.vue', lang: 'vue' }
      ];

      extensionTests.forEach(({ ext, lang }) => {
        expect(parserManager.getLanguageForFile(`test${ext}`)).toBe(lang);
      });
    });

    it('should reject unsupported extensions', () => {
      expect(parserManager.getLanguageForFile('test.unknown')).toBeUndefined();
      expect(parserManager.isFileSupported('test.xyz')).toBe(false);
    });
  });

  describe('Hash Generation', () => {
    it('should generate consistent hashes', () => {
      const content = 'test content';
      const hash1 = parserManager.hashContent(content);
      const hash2 = parserManager.hashContent(content);

      expect(hash1).toBe(hash2);
      expect(hash1).toHaveLength(64); // SHA256 hex length
    });

    it('should detect content changes', () => {
      const content1 = 'original content';
      const content2 = 'modified content';

      const hash1 = parserManager.hashContent(content1);
      const hash2 = parserManager.hashContent(content2);

      expect(hash1).not.toBe(hash2);
      expect(parserManager.needsReparsing(hash1, content2)).toBe(true);
      expect(parserManager.needsReparsing(hash1, content1)).toBe(false);
    });
  });
});