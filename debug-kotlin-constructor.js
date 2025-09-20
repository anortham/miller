import { ParserManager } from './src/parser/parser-manager.ts';

async function debugKotlinConstructor() {
  console.log('🔍 Debugging Kotlin constructor parameters...');

  const parserManager = new ParserManager();
  await parserManager.initialize();

  const kotlinCode = `
class Vehicle(
    val brand: String,
    private var speed: Int = 0
) {
    fun accelerate() {
        speed += 10
    }
}
`;

  try {
    const result = await parserManager.parseFile('test.kt', kotlinCode);

    function printNode(node, depth = 0) {
      const indent = '  '.repeat(depth);
      console.log(`${indent}${node.type} "${node.text.slice(0, 60).replace(/\n/g, '\\n')}"`);

      if (depth < 6) {
        for (const child of node.children) {
          printNode(child, depth + 1);
        }
      }
    }

    console.log('\n🌳 AST Structure:');
    printNode(result.tree.rootNode);

  } catch (error) {
    console.error('❌ Error:', error);
  }
}

debugKotlinConstructor();