import { ParserManager } from './src/parser/parser-manager.ts';
import { KotlinExtractor } from './src/extractors/kotlin-extractor.ts';

async function testInterfaceProperty() {
  console.log('🔍 Testing interface property extraction...');

  const parserManager = new ParserManager();
  await parserManager.initialize();

  const kotlinCode = `
interface Drawable {
    val color: String
    fun draw()
}
`;

  try {
    const result = await parserManager.parseFile('test.kt', kotlinCode);
    const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
    const symbols = extractor.extractSymbols(result.tree);

    console.log(`🔍 Found ${symbols.length} symbols:`);
    symbols.forEach(symbol => {
      console.log(`  - ${symbol.name} (${symbol.kind}) "${symbol.signature}" parent: ${symbol.parentId || 'none'}`);
    });

    // Check for interface properties
    const drawable = symbols.find(s => s.name === 'Drawable');
    const color = symbols.find(s => s.name === 'color' && s.parentId === drawable?.id);
    const draw = symbols.find(s => s.name === 'draw' && s.parentId === drawable?.id);

    console.log('\n🎯 Results:');
    console.log(`  Drawable: ${drawable ? '✅' : '❌'}`);
    console.log(`  color property: ${color ? '✅' : '❌'} - ${color?.signature || 'not found'}`);
    console.log(`  draw method: ${draw ? '✅' : '❌'} - ${draw?.signature || 'not found'}`);

  } catch (error) {
    console.error('❌ Error:', error);
  }
}

testInterfaceProperty();