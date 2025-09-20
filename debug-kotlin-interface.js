import { ParserManager } from './src/parser/parser-manager.ts';
import fs from 'fs';

async function debugKotlinInterface() {
  console.log('🔍 Testing Kotlin interface detection...');

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
    fun showTooltip() = println("tooltip")
}
`;

  console.log('📄 Kotlin interface code:');
  console.log(kotlinCode);

  try {
    const result = await parserManager.parseFile('test.kt', kotlinCode);
    console.log('✅ Parse successful');

    function printNode(node, depth = 0) {
      const indent = '  '.repeat(depth);
      console.log(`${indent}${node.type} "${node.text.slice(0, 50).replace(/\n/g, '\\n')}"`);

      if (depth < 4) {
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

debugKotlinInterface();