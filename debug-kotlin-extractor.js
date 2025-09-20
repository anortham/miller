import { ParserManager } from './src/parser/parser-manager.ts';
import { KotlinExtractor } from './src/extractors/kotlin-extractor.ts';
import fs from 'fs';

async function debugKotlinExtractor() {
  console.log('🔍 Testing Kotlin extractor directly...');

  const parserManager = new ParserManager();
  await parserManager.initialize();

  const kotlinCode = fs.readFileSync('test-workspace-real/kotlin/Main.kt', 'utf8');
  console.log('📄 Testing Main.kt:');
  console.log(kotlinCode);

  try {
    const result = await parserManager.parseFile('Main.kt', kotlinCode);
    console.log('✅ Parse successful');

    const extractor = new KotlinExtractor('kotlin', 'Main.kt', kotlinCode);
    const symbols = extractor.extractSymbols(result.tree);

    console.log(`🔍 Found ${symbols.length} symbols:`);
    symbols.forEach(symbol => {
      console.log(`  - ${symbol.name} (${symbol.kind}) ${symbol.signature || ''}`);
    });

    if (symbols.length === 0) {
      console.log('❌ No symbols found! Debugging tree traversal...');

      // Let's manually check what happens in the visitNode function
      function debugVisitNode(node, depth = 0) {
        const indent = '  '.repeat(depth);
        console.log(`${indent}Visiting: ${node.type} "${node.text.slice(0, 30).replace(/\n/g, '\\n')}"`);

        if (node.type === 'class_declaration') {
          console.log(`${indent}🎯 Found class_declaration! This should be extracted.`);
        }

        if (depth < 3) {
          for (const child of node.children) {
            debugVisitNode(child, depth + 1);
          }
        }
      }

      console.log('\n🌳 Tree traversal debug:');
      debugVisitNode(result.tree.rootNode);
    }

  } catch (error) {
    console.error('❌ Error:', error);
  }
}

debugKotlinExtractor();