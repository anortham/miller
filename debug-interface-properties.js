import { ParserManager } from './src/parser/parser-manager.ts';

async function debugInterfaceProperties() {
  console.log('🔍 Debugging interface properties AST...');

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

    function printNode(node, depth = 0) {
      const indent = '  '.repeat(depth);
      console.log(`${indent}${node.type} "${node.text.slice(0, 50).replace(/\n/g, '\\n')}"`);

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

debugInterfaceProperties();