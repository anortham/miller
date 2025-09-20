import { ParserManager } from './src/parser/parser-manager.ts';
import fs from 'fs';

async function debugKotlin() {
  console.log('🔍 Debugging Kotlin parser...');

  const parserManager = new ParserManager();
  await parserManager.initialize();

  const kotlinCode = fs.readFileSync('test-workspace-real/kotlin/Main.kt', 'utf8');
  console.log('📄 Kotlin code:');
  console.log(kotlinCode);
  console.log('\n🌳 Parsing...');

  try {
    const result = await parserManager.parseFile('Model.kt', kotlinCode);
    console.log('✅ Parse successful');

    // Print tree structure
    function printNode(node, depth = 0) {
      const indent = '  '.repeat(depth);
      console.log(`${indent}${node.type} "${node.text.slice(0, 30).replace(/\n/g, '\\n')}"`);

      if (depth < 4) { // Limit depth to avoid too much output
        for (const child of node.children) {
          printNode(child, depth + 1);
        }
      }
    }

    console.log('\n🌳 AST Structure:');
    printNode(result.tree.rootNode);

  } catch (error) {
    console.error('❌ Parse failed:', error);
  }
}

debugKotlin();