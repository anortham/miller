import { ParserManager } from './src/parser/parser-manager.ts';
import { KotlinExtractor } from './src/extractors/kotlin-extractor.ts';

async function testInterfaceFix() {
  console.log('🔍 Testing Kotlin interface fix...');

  const parserManager = new ParserManager();
  await parserManager.initialize();

  const kotlinCode = `
interface Drawable {
    fun draw()
}

class Shape {
    fun area(): Double = 0.0
}

interface Clickable {
    fun click()
}
`;

  try {
    const result = await parserManager.parseFile('test.kt', kotlinCode);
    const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
    const symbols = extractor.extractSymbols(result.tree);

    console.log(`🔍 Found ${symbols.length} symbols:`);
    symbols.forEach(symbol => {
      console.log(`  - ${symbol.name} (${symbol.kind}) "${symbol.signature}"`);
    });

    // Test specific interface detection
    const drawable = symbols.find(s => s.name === 'Drawable');
    const shape = symbols.find(s => s.name === 'Shape');
    const clickable = symbols.find(s => s.name === 'Clickable');

    console.log('\n🎯 Interface Detection Results:');
    console.log(`  Drawable: ${drawable?.kind} (should be interface) ✅`);
    console.log(`  Shape: ${shape?.kind} (should be class) ✅`);
    console.log(`  Clickable: ${clickable?.kind} (should be interface) ✅`);

  } catch (error) {
    console.error('❌ Error:', error);
  }
}

testInterfaceFix();