import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * PowerShell language extractor that handles PowerShell-specific constructs for Windows/Azure DevOps:
 * - Functions (simple and advanced with [CmdletBinding()])
 * - Variables (scoped, environment, automatic variables)
 * - Classes, methods, properties, and enums (PowerShell 5.0+)
 * - Azure PowerShell cmdlets and Windows management commands
 * - Module imports, exports, and using statements
 * - Parameter definitions with attributes and validation
 * - Cross-platform DevOps tool calls (docker, kubectl, az CLI)
 *
 * Special focus on Windows/Azure DevOps tracing to complement Bash for complete
 * cross-platform deployment automation coverage.
 */
export class PowerShellExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    this.walkTreeForSymbols(tree.rootNode, symbols);
    return symbols;
  }

  private walkTreeForSymbols(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    const symbol = this.extractSymbolFromNode(node, parentId);
    if (symbol) {
      symbols.push(symbol);

      // If this is a function, extract its parameters
      if (symbol.kind === SymbolKind.Function) {
        const parameters = this.extractFunctionParameters(node, symbol.id);
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
        case 'function_statement':
          return this.extractFunction(node, parentId);
        case 'param_block':
          return this.extractAdvancedFunction(node, parentId);
        case 'assignment_expression':
          return this.extractVariable(node, parentId);
        case 'variable':
          return this.extractVariableReference(node, parentId);
        case 'class_statement':
          return this.extractClass(node, parentId);
        case 'class_method_definition':
          return this.extractMethod(node, parentId);
        case 'class_property_definition':
          return this.extractProperty(node, parentId);
        case 'enum_statement':
          return this.extractEnum(node, parentId);
        case 'enum_member':
          return this.extractEnumMember(node, parentId);
        case 'import_statement':
        case 'using_statement':
          return this.extractImport(node, parentId);
        case 'command':
        case 'command_expression':
        case 'pipeline':
          return this.extractCommand(node, parentId);
        default:
          return null;
      }
    } catch (error) {
      console.warn(`Error extracting PowerShell symbol from ${node.type}:`, error);
      return null;
    }
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findFunctionNameNode(node);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's an advanced function with [CmdletBinding()]
    const isAdvanced = this.hasAttribute(node, 'CmdletBinding');

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature: this.extractFunctionSignature(node),
      visibility: 'public', // PowerShell functions are generally public
      parentId,
      docComment: isAdvanced ? 'Advanced PowerShell function with [CmdletBinding()]' : undefined
    });
  }

  private extractAdvancedFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // For param_block nodes (advanced functions), extract function name from ERROR node content
    const functionName = this.extractFunctionNameFromParamBlock(node);
    if (!functionName) return null;

    // Check for CmdletBinding attribute
    const hasCmdletBinding = this.hasAttribute(node, 'CmdletBinding');

    return this.createSymbol(node, functionName, SymbolKind.Function, {
      signature: this.extractAdvancedFunctionSignature(node, functionName),
      visibility: 'public',
      parentId,
      docComment: 'Advanced PowerShell function with [CmdletBinding()]'
    });
  }

  private extractFunctionParameters(funcNode: Parser.SyntaxNode, parentId: string): Symbol[] {
    const parameters: Symbol[] = [];

    // Handle simple functions - look for param_block with parameter_definition
    const paramBlocks = this.findNodesByType(funcNode, 'param_block');
    for (const paramBlock of paramBlocks) {
      const paramDefs = this.findNodesByType(paramBlock, 'parameter_definition');

      for (const paramDef of paramDefs) {
        const nameNode = this.findParameterNameNode(paramDef);
        if (!nameNode) continue;

        const paramName = this.getNodeText(nameNode).replace('$', '');
        const isMandatory = this.hasParameterAttribute(paramDef, 'Mandatory');

        const paramSymbol = this.createSymbol(paramDef, paramName, SymbolKind.Variable, {
          signature: this.extractParameterSignature(paramDef),
          visibility: 'public',
          parentId,
          docComment: isMandatory ? 'Mandatory parameter' : 'Optional parameter'
        });

        parameters.push(paramSymbol);
      }
    }

    // Handle advanced functions - look for parameter_list with script_parameter
    const paramLists = this.findNodesByType(funcNode, 'parameter_list');
    for (const paramList of paramLists) {
      const scriptParams = this.findNodesByType(paramList, 'script_parameter');

      for (const scriptParam of scriptParams) {
        // Find the direct child variable node (the parameter name), not any variable in the subtree
        const variableNode = scriptParam.children.find(child => child.type === 'variable');
        if (!variableNode) continue;

        const paramName = this.getNodeText(variableNode).replace('$', '');
        const isMandatory = this.hasParameterAttribute(scriptParam, 'Mandatory');

        const paramSymbol = this.createSymbol(scriptParam, paramName, SymbolKind.Variable, {
          signature: this.extractScriptParameterSignature(scriptParam),
          visibility: 'public',
          parentId,
          docComment: isMandatory ? 'Mandatory parameter' : 'Optional parameter'
        });

        parameters.push(paramSymbol);
      }
    }

    return parameters;
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findVariableNameNode(node);
    if (!nameNode) return null;

    let name = this.getNodeText(nameNode);

    // Remove $ prefix and scope qualifiers
    name = name.replace(/^\$/, '').replace(/^(Global|Script|Local|Using):/i, '');

    // Determine scope and visibility
    const fullText = this.getNodeText(nameNode);
    const isGlobal = fullText.includes('Global:');
    const isScript = fullText.includes('Script:');
    const isEnvironment = fullText.includes('env:') || this.isEnvironmentVariable(name);
    const isAutomatic = this.isAutomaticVariable(name);

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature: this.extractVariableSignature(node),
      visibility: isGlobal ? 'public' : 'private',
      parentId,
      docComment: this.getVariableDocumentation(isEnvironment, isAutomatic, isGlobal, isScript)
    });
  }

  private extractVariableReference(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    let name = this.getNodeText(node);

    // Remove $ prefix and scope qualifiers
    name = name.replace(/^\$/, '').replace(/^(Global|Script|Local|Using|env):/i, '');

    // Only extract automatic variables, environment variables, and special variables
    // to avoid creating symbols for every variable reference
    const isAutomatic = this.isAutomaticVariable(name);
    const isEnvironment = this.isEnvironmentVariable(name) || this.getNodeText(node).includes('env:');

    if (!isAutomatic && !isEnvironment) {
      return null; // Skip regular variable references
    }

    // Determine scope and visibility
    const fullText = this.getNodeText(node);
    const isGlobal = isAutomatic || fullText.includes('Global:');

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature: fullText, // Use the full variable reference as signature
      visibility: isGlobal ? 'public' : 'private',
      parentId,
      docComment: this.getVariableDocumentation(isEnvironment, isAutomatic, isGlobal, false)
    });
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findClassNameNode(node);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature: this.extractClassSignature(node),
      visibility: 'public',
      parentId
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findMethodNameNode(node);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isStatic = this.hasModifier(node, 'static');
    const isHidden = this.hasModifier(node, 'hidden');

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature: this.extractMethodSignature(node),
      visibility: isHidden ? 'private' : 'public',
      parentId,
      docComment: isStatic ? 'Static method' : undefined
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findPropertyNameNode(node);
    if (!nameNode) return null;

    let name = this.getNodeText(nameNode);
    name = name.replace(/^\$/, ''); // Remove $ prefix

    const isHidden = this.hasModifier(node, 'hidden');

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature: this.extractPropertySignature(node),
      visibility: isHidden ? 'private' : 'public',
      parentId
    });
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findEnumNameNode(node);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    return this.createSymbol(node, name, SymbolKind.Enum, {
      signature: `enum ${name}`,
      visibility: 'public',
      parentId
    });
  }

  private extractEnumMember(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findEnumMemberNameNode(node);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const value = this.extractEnumMemberValue(node);

    return this.createSymbol(node, name, SymbolKind.EnumMember, {
      signature: value ? `${name} = ${value}` : name,
      visibility: 'public',
      parentId
    });
  }

  private extractImport(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const moduleNameNode = this.findModuleNameNode(node);
    if (!moduleNameNode) return null;

    let moduleName = this.getNodeText(moduleNameNode);
    moduleName = moduleName.replace(/['"]/g, ''); // Remove quotes

    const nodeText = this.getNodeText(node);
    const isUsing = nodeText.startsWith('using');
    const isDotSourcing = nodeText.startsWith('.');

    return this.createSymbol(node, moduleName, SymbolKind.Import, {
      signature: nodeText.trim(),
      visibility: 'public',
      parentId,
      docComment: isUsing ? 'Using statement' : isDotSourcing ? 'Dot sourcing' : 'Module import'
    });
  }

  private extractCommand(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Check for dot sourcing first (special case with different AST structure)
    const dotSourceNode = node.children.find(child => child.type === 'command_invokation_operator' && this.getNodeText(child) === '.');
    if (dotSourceNode) {
      return this.extractDotSourcing(node, parentId);
    }

    const commandNameNode = this.findCommandNameNode(node);
    if (!commandNameNode) return null;

    const commandName = this.getNodeText(commandNameNode);

    // Check for import/module commands first
    const importCommands = ['Import-Module', 'Export-ModuleMember', 'using'];
    if (importCommands.includes(commandName)) {
      return this.extractImportCommand(node, commandName, parentId);
    }

    // Focus on Azure, Windows, and cross-platform DevOps commands
    const devopsCommands = [
      // Azure PowerShell
      'Connect-AzAccount', 'Set-AzContext', 'New-AzResourceGroup', 'New-AzResourceGroupDeployment',
      'New-AzContainerGroup', 'New-AzAksCluster', 'Get-AzAksCluster',

      // Windows Management
      'Enable-WindowsOptionalFeature', 'Install-WindowsFeature', 'Set-ItemProperty',
      'Set-Service', 'Start-Service', 'New-Item', 'Copy-Item',

      // Cross-platform DevOps
      'docker', 'kubectl', 'az',

      // PowerShell Core
      'Invoke-Command'
    ];

    const isInteresting = devopsCommands.some(cmd => commandName.includes(cmd)) ||
                         commandName.startsWith('Connect-') ||
                         commandName.startsWith('New-') ||
                         commandName.startsWith('Set-') ||
                         commandName.startsWith('Get-');

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

  private extractImportCommand(node: Parser.SyntaxNode, commandName: string, parentId?: string): Symbol | null {
    const nodeText = this.getNodeText(node);
    let moduleName = '';
    let signature = nodeText.trim();

    if (commandName === 'Import-Module') {
      // Extract module name from "Import-Module Az.Accounts" or "Import-Module -Name 'Custom.Tools'"
      const nameMatch = nodeText.match(/Import-Module\s+(?:-Name\s+["']?([^"'\s]+)["']?|([A-Za-z0-9.-]+))/);
      moduleName = nameMatch ? (nameMatch[1] || nameMatch[2]) : 'unknown';
    } else if (commandName === 'using') {
      // Extract from "using namespace System.Collections.Generic" or "using module Az.Storage"
      const usingMatch = nodeText.match(/using\s+(?:namespace|module)\s+([A-Za-z0-9.-_]+)/);
      moduleName = usingMatch ? usingMatch[1] : 'unknown';
    } else if (commandName === 'Export-ModuleMember') {
      // Extract the type being exported (Function, Variable, Alias)
      const exportMatch = nodeText.match(/Export-ModuleMember\s+-(\w+)/i);
      if (exportMatch) {
        moduleName = exportMatch[1];
      } else {
        // Fallback: try to extract from the full text
        if (nodeText.includes('-Function')) moduleName = 'Function';
        else if (nodeText.includes('-Variable')) moduleName = 'Variable';
        else if (nodeText.includes('-Alias')) moduleName = 'Alias';
        else moduleName = 'ModuleMember';
      }
    }

    if (!moduleName || moduleName === 'unknown') {
      return null;
    }

    const isUsing = commandName === 'using';
    const isExport = commandName === 'Export-ModuleMember';

    return this.createSymbol(node, moduleName, isExport ? SymbolKind.Export : SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId,
      docComment: isExport ? 'Module export' : isUsing ? 'Using statement' : 'Module import'
    });
  }

  private extractDotSourcing(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract script path from dot sourcing like '. "$PSScriptRoot\CommonFunctions.ps1"'
    const commandNameExprNode = node.children.find(child => child.type === 'command_name_expr');
    if (!commandNameExprNode) return null;

    const scriptPath = this.getNodeText(commandNameExprNode);
    const signature = this.getNodeText(node).trim();

    // Extract just the filename for the symbol name
    let fileName = scriptPath.replace(/['"]/g, ''); // Remove quotes
    const lastSlash = Math.max(fileName.lastIndexOf('\\'), fileName.lastIndexOf('/'));
    if (lastSlash !== -1) {
      fileName = fileName.substring(lastSlash + 1);
    }

    // Remove .ps1 extension for cleaner symbol name
    if (fileName.endsWith('.ps1')) {
      fileName = fileName.substring(0, fileName.length - 4);
    }

    return this.createSymbol(node, fileName, SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId,
      docComment: 'Dot sourcing script'
    });
  }

  private extractFunctionNameFromParamBlock(node: Parser.SyntaxNode): string | null {
    // For param_block nodes inside advanced functions, we need to look up the tree
    // to find the ERROR node that contains the function declaration

    // First, try to find ERROR node at program level (parent's parent's parent typically)
    let current: Parser.SyntaxNode | null = node;
    while (current && current.type !== 'program') {
      current = current.parent;
    }

    if (current && current.type === 'program') {
      // Look for ERROR node in program children
      for (const child of current.children) {
        if (child.type === 'ERROR') {
          const text = this.getNodeText(child);
          // Extract function name from text like "\nfunction Set-CustomProperty {"
          const match = text.match(/function\s+([A-Za-z][A-Za-z0-9-_]*)/);
          if (match) {
            return match[1];
          }
        }
      }
    }

    // Fallback: look in parent nodes for any ERROR containing function
    current = node.parent;
    while (current) {
      if (current.type === 'ERROR') {
        const text = this.getNodeText(current);
        const match = text.match(/function\s+([A-Za-z][A-Za-z0-9-_]*)/);
        if (match) {
          return match[1];
        }
      }
      current = current.parent;
    }

    return null;
  }

  private extractAdvancedFunctionSignature(node: Parser.SyntaxNode, functionName: string): string {
    const hasCmdletBinding = this.hasAttribute(node, 'CmdletBinding');
    const hasOutputType = this.hasAttribute(node, 'OutputType');

    let signature = '';
    if (hasCmdletBinding) signature += '[CmdletBinding()] ';
    if (hasOutputType) signature += '[OutputType([void])] ';
    signature += `function ${functionName}()`;

    return signature;
  }

  // Helper methods for node finding
  private findFunctionNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for function name in various possible locations
    for (const child of node.children) {
      if (child.type === 'function_name' || child.type === 'identifier' || child.type === 'cmdlet_name') {
        return child;
      }
    }
    return null;
  }

  private findVariableNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'left_assignment_expression' || child.type === 'variable' || child.type === 'identifier') {
        return child;
      }
    }
    return null;
  }

  private findParameterNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'variable' || child.type === 'parameter_name') {
        return child;
      }
    }
    return null;
  }

  private findClassNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'simple_name' || child.type === 'identifier' || child.type === 'type_name') {
        return child;
      }
    }
    return null;
  }

  private findMethodNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'simple_name' || child.type === 'identifier' || child.type === 'method_name') {
        return child;
      }
    }
    return null;
  }

  private findPropertyNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'variable' || child.type === 'property_name' || child.type === 'identifier') {
        return child;
      }
    }
    return null;
  }

  private findEnumNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'simple_name' || child.type === 'identifier' || child.type === 'type_name') {
        return child;
      }
    }
    return null;
  }

  private findEnumMemberNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'simple_name' || child.type === 'identifier') {
        return child;
      }
    }
    return null;
  }

  private extractEnumMemberValue(node: Parser.SyntaxNode): string | null {
    // Look for assignment pattern: name = value
    for (let i = 0; i < node.children.length - 1; i++) {
      if (node.children[i].type === '=' && node.children[i + 1]) {
        return this.getNodeText(node.children[i + 1]);
      }
    }
    return null;
  }

  private findModuleNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'string' || child.type === 'identifier' || child.type === 'module_name') {
        return child;
      }
    }
    return null;
  }

  private findCommandNameNode(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === 'command_name' || child.type === 'identifier' || child.type === 'cmdlet_name') {
        return child;
      }
    }
    return null;
  }

  // Signature extraction methods
  private extractFunctionSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findFunctionNameNode(node);
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    const hasAttributes = this.hasAttribute(node, 'CmdletBinding');
    const prefix = hasAttributes ? '[CmdletBinding()] ' : '';

    return `${prefix}function ${name}()`;
  }

  private extractParameterSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findParameterNameNode(node);
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    const attributes = this.extractParameterAttributes(node);
    return attributes ? `${attributes} ${name}` : name;
  }

  private extractScriptParameterSignature(node: Parser.SyntaxNode): string {
    // Extract variable name
    const variableNode = this.findNodesByType(node, 'variable')[0];
    const name = variableNode ? this.getNodeText(variableNode) : '$unknown';

    // Extract type and attributes from attribute_list
    const attributeList = this.findNodesByType(node, 'attribute_list')[0];
    if (!attributeList) return name;

    const attributes: string[] = [];
    const attributeNodes = this.findNodesByType(attributeList, 'attribute');

    for (const attr of attributeNodes) {
      const attrText = this.getNodeText(attr);

      // Check if it's a Parameter attribute
      if (attrText.includes('Parameter')) {
        attributes.push(attrText);
      }
      // Check if it's a type (like [string], [switch])
      else if (attrText.match(/^\[.*\]$/)) {
        attributes.push(attrText);
      }
    }

    return attributes.length > 0 ? `${attributes.join(' ')} ${name}` : name;
  }

  private extractVariableSignature(node: Parser.SyntaxNode): string {
    const fullText = this.getNodeText(node);
    const equalIndex = fullText.indexOf('=');

    if (equalIndex !== -1 && equalIndex < fullText.length - 1) {
      return fullText.trim();
    }

    const nameNode = this.findVariableNameNode(node);
    return nameNode ? this.getNodeText(nameNode) : 'unknown';
  }

  private extractClassSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findClassNameNode(node);
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // Check for inheritance
    const inheritance = this.extractInheritance(node);
    return inheritance ? `class ${name} : ${inheritance}` : `class ${name}`;
  }

  private extractMethodSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findMethodNameNode(node);
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    const returnType = this.extractReturnType(node);
    const isStatic = this.hasModifier(node, 'static');

    const prefix = isStatic ? 'static ' : '';
    const suffix = returnType ? ` ${returnType}` : '';

    return `${prefix}${suffix} ${name}()`;
  }

  private extractPropertySignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findPropertyNameNode(node);
    const name = nameNode ? this.getNodeText(nameNode).replace('$', '') : 'unknown';

    const type = this.extractPropertyType(node);
    const isHidden = this.hasModifier(node, 'hidden');

    const prefix = isHidden ? 'hidden ' : '';
    return type ? `${prefix}${type}$${name}` : `${prefix}$${name}`;
  }

  private extractCommandSignature(node: Parser.SyntaxNode): string {
    const commandText = this.getNodeText(node);
    return commandText.length > 100 ?
      commandText.substring(0, 97) + '...' :
      commandText;
  }

  // Helper methods for attributes and modifiers
  private hasAttribute(node: Parser.SyntaxNode, attributeName: string): boolean {
    const nodeText = this.getNodeText(node);
    return nodeText.includes(`[${attributeName}`);
  }

  private hasParameterAttribute(node: Parser.SyntaxNode, attributeName: string): boolean {
    const nodeText = this.getNodeText(node);
    return nodeText.includes(`${attributeName}=$true`) || nodeText.includes(`${attributeName}=true`);
  }

  private hasModifier(node: Parser.SyntaxNode, modifier: string): boolean {
    const nodeText = this.getNodeText(node);
    return nodeText.includes(modifier);
  }

  private extractParameterAttributes(node: Parser.SyntaxNode): string {
    const nodeText = this.getNodeText(node);
    const match = nodeText.match(/\[Parameter[^\]]*\]/);
    return match ? match[0] : '';
  }

  private extractInheritance(node: Parser.SyntaxNode): string | null {
    const nodeText = this.getNodeText(node);
    const match = nodeText.match(/:\s*(\w+)/);
    return match ? match[1] : null;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    const nodeText = this.getNodeText(node);
    const match = nodeText.match(/\[(\w+)\]/);
    return match ? `[${match[1]}]` : null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    const nodeText = this.getNodeText(node);
    const match = nodeText.match(/\[(\w+)\]/);
    return match ? `[${match[1]}]` : null;
  }

  // Variable classification methods
  private isEnvironmentVariable(name: string): boolean {
    const envVars = [
      'PATH', 'COMPUTERNAME', 'USERNAME', 'TEMP', 'TMP', 'USERPROFILE',
      'AZURE_CLIENT_ID', 'AZURE_CLIENT_SECRET', 'AZURE_TENANT_ID',
      'POWERSHELL_TELEMETRY_OPTOUT'
    ];
    return envVars.includes(name) || name.match(/^[A-Z_][A-Z0-9_]*$/);
  }

  private isAutomaticVariable(name: string): boolean {
    const autoVars = [
      'PSVersionTable', 'PWD', 'LASTEXITCODE', 'Error', 'Host', 'Profile',
      'PSScriptRoot', 'PSCommandPath', 'MyInvocation', 'Args', 'Input'
    ];
    return autoVars.includes(name);
  }

  private getVariableDocumentation(isEnvironment: boolean, isAutomatic: boolean, isGlobal: boolean, isScript: boolean): string {
    const annotations: string[] = [];

    if (isEnvironment) annotations.push('Environment Variable');
    if (isAutomatic) annotations.push('Automatic Variable');
    if (isGlobal) annotations.push('Global Scope');
    if (isScript) annotations.push('Script Scope');

    return annotations.length > 0 ? `[${annotations.join(', ')}]` : '';
  }

  private getCommandDocumentation(commandName: string): string {
    const commandDocs: Record<string, string> = {
      'Connect-AzAccount': '[Azure CLI Call]',
      'Set-AzContext': '[Azure Context Management]',
      'New-AzResourceGroup': '[Azure Resource Management]',
      'New-AzResourceGroupDeployment': '[Azure Deployment]',
      'docker': '[Docker Container Call]',
      'kubectl': '[Kubernetes CLI Call]',
      'az': '[Azure CLI Call]',
      'Import-Module': '[PowerShell Module Import]',
      'Export-ModuleMember': '[PowerShell Module Export]',
      'Invoke-Command': '[PowerShell Remoting]'
    };

    // Pattern matching for commands
    if (commandName.startsWith('Connect-Az')) return '[Azure CLI Call]';
    if (commandName.startsWith('New-Az')) return '[Azure Resource Creation]';
    if (commandName.startsWith('Set-Az')) return '[Azure Configuration]';
    if (commandName.startsWith('Get-Az')) return '[Azure Information Retrieval]';
    if (commandName.includes('WindowsFeature')) return '[Windows Feature Management]';
    if (commandName.includes('Service')) return '[Windows Service Management]';

    return commandDocs[commandName] || '[PowerShell Command]';
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    this.walkTreeForRelationships(tree.rootNode, symbols, relationships);
    return relationships;
  }

  private walkTreeForRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    try {
      switch (node.type) {
        case 'command_expression':
        case 'pipeline_expression':
          this.extractCommandRelationships(node, symbols, relationships);
          break;
        case 'class_definition':
          this.extractInheritanceRelationships(node, symbols, relationships);
          break;
      }
    } catch (error) {
      console.warn(`Error extracting PowerShell relationship from ${node.type}:`, error);
    }

    for (const child of node.children) {
      this.walkTreeForRelationships(child, symbols, relationships);
    }
  }

  private extractCommandRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    const commandNameNode = this.findCommandNameNode(node);
    if (!commandNameNode) return;

    const commandName = this.getNodeText(commandNameNode);
    const commandSymbol = symbols.find(s => s.name === commandName && s.kind === SymbolKind.Function);

    if (commandSymbol) {
      // Find the parent function that calls this command
      let current = node.parent;
      while (current && current.type !== 'function_definition') {
        current = current.parent;
      }

      if (current) {
        const funcNameNode = this.findFunctionNameNode(current);
        if (funcNameNode) {
          const funcName = this.getNodeText(funcNameNode);
          const funcSymbol = symbols.find(s => s.name === funcName && s.kind === SymbolKind.Function);

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

  private extractInheritanceRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    const inheritance = this.extractInheritance(node);
    if (!inheritance) return;

    const classNameNode = this.findClassNameNode(node);
    if (!classNameNode) return;

    const className = this.getNodeText(classNameNode);
    const childClass = symbols.find(s => s.name === className && s.kind === SymbolKind.Class);
    const parentClass = symbols.find(s => s.name === inheritance && s.kind === SymbolKind.Class);

    if (childClass && parentClass) {
      relationships.push(this.createRelationship(
        childClass.id,
        parentClass.id,
        RelationshipKind.Extends,
        node
      ));
    }
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    for (const symbol of symbols) {
      if (symbol.kind === SymbolKind.Variable || symbol.kind === SymbolKind.Property) {
        const signature = symbol.signature || '';
        let type = 'object';

        // Extract type from PowerShell type annotations
        const typeMatch = signature.match(/\[(\w+)\]/);
        if (typeMatch) {
          type = typeMatch[1].toLowerCase();
        } else if (signature.includes('=')) {
          // Infer from value
          const value = signature.split('=')[1]?.trim() || '';
          if (value.match(/^\d+$/)) type = 'int';
          else if (value.match(/^\d+\.\d+$/)) type = 'double';
          else if (value.match(/^\$(true|false)$/i)) type = 'bool';
          else if (value.startsWith('"') || value.startsWith("'")) type = 'string';
          else if (value.startsWith('@(')) type = 'array';
          else if (value.startsWith('@{')) type = 'hashtable';
        }

        types.set(symbol.name, type);
      }
    }

    return types;
  }
}