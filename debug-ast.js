import { ParserManager } from './src/parser/parser-manager.ts';

const parserManager = new ParserManager();
await parserManager.initialize();

const cppCode = `
static int static_var = 0;
extern int extern_var;
`;

const result = await parserManager.parseFile('test.cpp', cppCode);

function printAST(node, depth = 0) {
  const indent = '  '.repeat(depth);
  console.log(`${indent}${node.type}: "${node.text.replace(/\n/g, '\\n')}"`);

  if (node.children) {
    for (const child of node.children) {
      printAST(child, depth + 1);
    }
  }
}

console.log('AST for template function:');
printAST(result.tree.rootNode);