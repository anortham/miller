import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';
import { log, LogLevel } from '../utils/logger.js';

/**
 * Bash language extractor that handles Bash/shell-specific constructs for DevOps tracing:
 * - Functions and their definitions
 * - Variables (local, environment, exported)
 * - External command calls (critical for cross-language tracing!)
 * - Script arguments and parameters
 * - Conditional logic and loops
 * - Source/include relationships
 * - Docker, kubectl, npm, and other DevOps tool calls
 *
 * Special focus on cross-language tracing since Bash scripts often orchestrate
 * other programs (Python, Node.js, Go binaries, Docker containers, etc.).
 */
export class BashExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    this.walkTreeForSymbols(tree.rootNode, symbols);
    return symbols;
  }

  private walkTreeForSymbols(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    const symbol = this.extractSymbolFromNode(node, parentId);
    if (symbol) {
      symbols.push(symbol);

      // If this is a function, extract its positional parameters
      if (symbol.kind === SymbolKind.Function) {
        const parameters = this.extractPositionalParameters(node, symbol.id);
        symbols.push(...parameters);
      }

      parentId = symbol.id;
    }

    for (const child of node.children) {
      this.walkTreeForSymbols(child, symbols, parentId);
    }
  }

  private extractSymbolFromNode(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    try {
      switch (node.type) {
        case 'function_definition':
          return this.extractFunction(node, parentId);
        case 'variable_assignment':
          return this.extractVariable(node, parentId);
        case 'declaration_command': // declare, export, readonly
          return this.extractDeclaration(node, parentId);
        case 'command':
        case 'simple_command':
          return this.extractCommand(node, parentId);
        case 'for_statement':
        case 'while_statement':
        case 'if_statement':
          return this.extractControlFlow(node, parentId);
        default:
          return null;
      }
    } catch (error) {
      log.extractor(LogLevel.WARN, `Error extracting Bash symbol from ${node.type}:`, error);
      return null;
    }
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findNameNode(node);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature: this.extractFunctionSignature(node),
      visibility: 'public', // Bash functions are generally accessible within the script
      parentId,
      docComment: this.findDocComment(node)
    });
  }

  private extractPositionalParameters(funcNode: Parser.SyntaxNode, parentId: string): Symbol[] {
    const parameters: Symbol[] = [];
    const seenParams = new Set<string>();

    this.walkTree(funcNode, (node) => {
      if (node.type === 'simple_expansion' || node.type === 'expansion') {
        const paramText = this.getNodeText(node);
        const paramMatch = paramText.match(/\$(\d+)/);

        if (paramMatch) {
          const paramNumber = paramMatch[1];
          const paramName = `$${paramNumber}`;

          if (!seenParams.has(paramName)) {
            seenParams.add(paramName);

            const paramSymbol = this.createSymbol(node, paramName, SymbolKind.Variable, {
              signature: `${paramName} (positional parameter)`,
              visibility: 'public',
              parentId
            });

            parameters.push(paramSymbol);
            // Add to the main symbol map for later retrieval
          }
        }
      }
    });

    return parameters;
  }

  private extractFunctionParameters(funcNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    // Bash functions use positional parameters $1, $2, etc.
    // We can look for parameter expansions within the function body
    this.traverseTree(funcNode, (node) => {
      if (node.type === 'simple_expansion' || node.type === 'expansion') {
        const paramText = this.getNodeText(node);
        const paramMatch = paramText.match(/\$(\d+)/);

        if (paramMatch) {
          const paramNumber = paramMatch[1];
          const paramName = `$${paramNumber}`;

          // Only add if we haven't seen this parameter before
          const existingParam = symbols.find(s =>
            s.parentId === parentId && s.name === paramName
          );

          if (!existingParam) {
            const paramSymbol: Symbol = {
              id: this.generateId(`${paramName}_param`, node.startPosition),
              name: paramName,
              kind: SymbolKind.Variable,
              signature: `${paramName} (positional parameter)`,
              startLine: node.startPosition.row,
              startColumn: node.startPosition.column,
              endLine: node.endPosition.row,
              endColumn: node.endPosition.column,
              filePath: this.filePath,
              language: this.language,
              parentId,
              visibility: 'public'
            };

            symbols.push(paramSymbol);
          }
        }
      }
    });
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findVariableNameNode(node);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's an environment variable or local variable
    const isEnvironment = this.isEnvironmentVariable(node, name);
    const isExported = this.isExportedVariable(node);

    return this.createSymbol(node, name, isEnvironment ? SymbolKind.Constant : SymbolKind.Variable, {
      signature: this.extractVariableSignature(node),
      visibility: isExported ? 'public' : 'private',
      parentId,
      docComment: this.extractVariableDocumentation(node, isEnvironment, isExported)
    });
  }

  private extractDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle declare, export, readonly commands
    const declarationType = this.getNodeText(node).split(' ')[0];

    // Look for variable assignments within the declaration
    const assignments = this.getChildrenOfType(node, 'variable_assignment');
    if (assignments.length > 0) {
      const assignment = assignments[0]; // Take first assignment
      const nameNode = this.findVariableNameNode(assignment);
      if (!nameNode) return null;

      const name = this.getNodeText(nameNode);

      // Check if it's readonly: either 'readonly' command or 'declare -r'
      const isReadonly = declarationType === 'readonly' ||
                        declarationType.includes('readonly') ||
                        (declarationType === 'declare' && this.getNodeText(node).includes(' -r '));

      // Check if it's an environment variable (but not if it's readonly)
      const isEnvironment = !isReadonly && this.isEnvironmentVariable(assignment, name);
      const isExported = declarationType === 'export';

      return this.createSymbol(assignment, name,
        isReadonly ? SymbolKind.Constant : SymbolKind.Variable, {
        signature: `${declarationType} ${name}`,
        visibility: isExported ? 'public' : 'private',
        parentId,
        docComment: this.extractVariableDocumentation(assignment, isEnvironment, isExported, isReadonly)
      });
    }

    return null;
  }

  private extractCommand(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract external commands - this is crucial for cross-language tracing!
    const commandNameNode = this.findCommandNameNode(node);
    if (!commandNameNode) return null;

    const commandName = this.getNodeText(commandNameNode);

    // Focus on commands that call other programs/languages
    const crossLanguageCommands = [
      'python', 'python3', 'node', 'npm', 'bun', 'deno',
      'go', 'cargo', 'rustc', 'java', 'javac', 'mvn',
      'dotnet', 'php', 'ruby', 'gem',
      'docker', 'kubectl', 'helm', 'terraform',
      'git', 'curl', 'wget', 'ssh', 'scp'
    ];

    const isInteresting = crossLanguageCommands.includes(commandName) ||
                         commandName.startsWith('./') ||
                         commandName.includes('/');

    if (isInteresting) {
      return this.createSymbol(node, commandName, SymbolKind.Function, {
        signature: this.extractCommandSignature(node),
        visibility: 'public',
        parentId,
        docComment: this.getCommandDocumentation(commandName)
      });
    }

    return null;
  }

  private extractControlFlow(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract control flow constructs for understanding script logic
    const controlType = node.type.replace('_statement', '');

    return this.createSymbol(node, `${controlType} block`, SymbolKind.Method, {
      signature: this.extractControlFlowSignature(node),
      visibility: 'private',
      parentId,
      docComment: `[${controlType.toUpperCase()} control flow]`
    });
  }

  // Helper methods for variable analysis
  private isEnvironmentVariable(node: Parser.SyntaxNode, name: string): boolean {
    // Common environment variables
    const envVars = [
      'PATH', 'HOME', 'USER', 'PWD', 'SHELL', 'TERM',
      'NODE_ENV', 'PYTHON_PATH', 'JAVA_HOME', 'GOPATH',
      'DOCKER_HOST', 'KUBECONFIG'
    ];

    return envVars.includes(name) || name.match(/^[A-Z_][A-Z0-9_]*$/);
  }

  private isExportedVariable(node: Parser.SyntaxNode): boolean {
    // Check if the assignment is preceded by 'export'
    let current = node.previousSibling;
    while (current) {
      const text = this.getNodeText(current);
      if (text === 'export') return true;
      current = current.previousSibling;
    }
    return false;
  }

  // Signature extraction methods
  private extractFunctionSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findNameNode(node);
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';
    return `function ${name}()`;
  }

  private extractVariableSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findVariableNameNode(node);
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // Get the full assignment text and extract value
    const fullText = this.getNodeText(node);
    const equalIndex = fullText.indexOf('=');

    if (equalIndex !== -1 && equalIndex < fullText.length - 1) {
      const value = fullText.substring(equalIndex + 1).trim();
      return `${name}=${value}`;
    }

    return name;
  }

  private extractCommandSignature(node: Parser.SyntaxNode): string {
    // Get the full command with arguments
    const commandText = this.getNodeText(node);

    // Limit length for readability
    return commandText.length > 100 ?
      commandText.substring(0, 97) + '...' :
      commandText;
  }

  private extractControlFlowSignature(node: Parser.SyntaxNode): string {
    const controlType = node.type.replace('_statement', '');

    // Try to extract the condition for if/while
    let conditionNode = null;
    for (const child of node.children) {
      if (child.type === 'test_command' || child.type === 'condition') {
        conditionNode = child;
        break;
      }
    }

    if (conditionNode) {
      const condition = this.getNodeText(conditionNode);
      return `${controlType} (${condition.length > 50 ? condition.substring(0, 47) + '...' : condition})`;
    }

    return `${controlType} block`;
  }

  // Documentation helpers
  private extractVariableDocumentation(node: Parser.SyntaxNode, isEnvironment: boolean, isExported: boolean, isReadonly?: boolean): string {
    const annotations: string[] = [];

    if (isReadonly) annotations.push('READONLY');
    if (isEnvironment) annotations.push('Environment Variable');
    if (isExported) annotations.push('Exported');

    return annotations.length > 0 ? `[${annotations.join(', ')}]` : '';
  }

  private getCommandDocumentation(commandName: string): string {
    const commandDocs: Record<string, string> = {
      'python': '[Python Interpreter Call]',
      'python3': '[Python 3 Interpreter Call]',
      'node': '[Node.js Runtime Call]',
      'npm': '[NPM Package Manager Call]',
      'bun': '[Bun Runtime Call]',
      'go': '[Go Command Call]',
      'cargo': '[Rust Cargo Call]',
      'java': '[Java Runtime Call]',
      'dotnet': '[.NET CLI Call]',
      'docker': '[Docker Container Call]',
      'kubectl': '[Kubernetes CLI Call]',
      'terraform': '[Infrastructure as Code Call]',
      'git': '[Version Control Call]'
    };

    return commandDocs[commandName] || '[External Program Call]';
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    this.walkTreeForRelationships(tree.rootNode, symbols, relationships);
    return relationships;
  }

  private walkTreeForRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    try {
      switch (node.type) {
        case 'command':
        case 'simple_command':
          this.extractCommandRelationships(node, symbols, relationships);
          break;
        case 'command_substitution':
          this.extractCommandSubstitutionRelationships(node, symbols, relationships);
          break;
        case 'file_redirect':
          this.extractFileRelationships(node, symbols, relationships);
          break;
      }
    } catch (error) {
      log.extractor(LogLevel.WARN, `Error extracting Bash relationship from ${node.type}:`, error);
    }

    for (const child of node.children) {
      this.walkTreeForRelationships(child, symbols, relationships);
    }
  }

  private extractCommandRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract relationships between functions and the commands they call
    const commandNameNode = this.findCommandNameNode(node);
    if (!commandNameNode) return;

    const commandName = this.getNodeText(commandNameNode);
    const commandSymbol = symbols.find(s =>
      s.name === commandName && s.kind === SymbolKind.Function
    );

    if (commandSymbol) {
      // Find the parent function that calls this command
      let current = node.parent;
      while (current && current.type !== 'function_definition') {
        current = current.parent;
      }

      if (current) {
        const funcNameNode = this.findNameNode(current);
        if (funcNameNode) {
          const funcName = this.getNodeText(funcNameNode);
          const funcSymbol = symbols.find(s =>
            s.name === funcName && s.kind === SymbolKind.Function
          );

          if (funcSymbol && funcSymbol.id !== commandSymbol.id) {
            relationships.push(this.createRelationship(
              funcSymbol.id,
              commandSymbol.id,
              RelationshipKind.Calls,
              node
            ));
          }
        }
      }
    }
  }

  private extractCommandSubstitutionRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract relationships for command substitutions $(command) or `command`
    // These show data flow dependencies
  }

  private extractFileRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract relationships for file redirections and pipes
    // These show data flow between commands
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    for (const symbol of symbols) {
      if (symbol.kind === SymbolKind.Variable || symbol.kind === SymbolKind.Constant) {
        // Infer type from signature
        const signature = symbol.signature || '';
        let type = 'string';

        if (signature.includes('=')) {
          let value = signature.split('=')[1]?.trim() || '';

          // Remove quotes if present
          value = value.replace(/^["']|["']$/g, '');

          if (value.match(/^\d+$/)) type = 'integer';
          else if (value.match(/^\d+\.\d+$/)) type = 'float';
          else if (value.match(/^(true|false)$/i)) type = 'boolean';
          else if (value.startsWith('/') || value.includes('/')) type = 'path';
        }

        types.set(symbol.name, type);
      }
    }

    return types;
  }

  // Helper methods for finding specific node types
  private findNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for function name nodes
    const nameField = node.childForFieldName('name');
    if (nameField) return nameField;

    // Fallback: look for 'word' or 'identifier' children
    for (const child of node.children) {
      if (child.type === 'word' || child.type === 'identifier') {
        return child;
      }
    }
    return null;
  }

  private findVariableNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for variable name in assignments
    const nameField = node.childForFieldName('name');
    if (nameField) return nameField;

    // Look for variable_name child
    for (const child of node.children) {
      if (child.type === 'variable_name') {
        return child;
      }
    }

    // Fallback: look for word child (first one usually)
    for (const child of node.children) {
      if (child.type === 'word') {
        return child;
      }
    }
    return null;
  }

  private findCommandNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for command name field
    const nameField = node.childForFieldName('name');
    if (nameField) return nameField;

    // Look for command_name child
    for (const child of node.children) {
      if (child.type === 'command_name') {
        return child;
      }
    }

    // Fallback: first word child
    for (const child of node.children) {
      if (child.type === 'word') {
        return child;
      }
    }
    return null;
  }
}