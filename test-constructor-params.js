import { ParserManager } from './src/parser/parser-manager.ts';
import { KotlinExtractor } from './src/extractors/kotlin-extractor.ts';

async function testConstructorParams() {
  console.log('🔍 Testing Kotlin constructor parameter extraction...');

  const parserManager = new ParserManager();
  await parserManager.initialize();

  const kotlinCode = `
class Vehicle(
    val brand: String,
    private var speed: Int = 0,
    protected val maxSpeed: Int
) {
    fun accelerate() {
        speed += 10
    }
}

class Person(name: String, age: Int) {
    private val fullName = name
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

    // Test specific constructor parameter detection
    const vehicleProperties = symbols.filter(s =>
      s.parentId && s.kind === 'property' &&
      symbols.find(parent => parent.id === s.parentId && parent.name === 'Vehicle')
    );

    const personProperties = symbols.filter(s =>
      s.parentId && s.kind === 'property' &&
      symbols.find(parent => parent.id === s.parentId && parent.name === 'Person')
    );

    console.log('\n🎯 Constructor Parameter Results:');
    console.log(`  Vehicle constructor params: ${vehicleProperties.length} (should be 3)`);
    vehicleProperties.forEach(prop => {
      console.log(`    - ${prop.name}: "${prop.signature}"`);
    });

    console.log(`  Person constructor params: ${personProperties.length} (should be 2)`);
    personProperties.forEach(prop => {
      console.log(`    - ${prop.name}: "${prop.signature}"`);
    });

  } catch (error) {
    console.error('❌ Error:', error);
  }
}

testConstructorParams();