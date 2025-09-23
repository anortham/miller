import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { PowerShellExtractor } from '../../extractors/powershell-extractor.js';
import { SymbolKind, RelationshipKind } from '../../extractors/base-extractor.js';

describe('PowerShellExtractor', () => {
  let parserManager: ParserManager;

    beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    const { MillerPaths } = await import('../../utils/miller-paths.js');
    const paths = new MillerPaths(process.cwd());
    await paths.ensureDirectories();
    initializeLogger(paths);

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Functions and Advanced Functions', () => {
    it('should extract PowerShell functions and advanced functions', async () => {
      const powershellCode = `
# Simple function
function Get-UserInfo {
    param($UserName)
    Write-Output "User: $UserName"
}

# Advanced function with CmdletBinding
function Get-ComputerData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$ComputerName,

        [Parameter()]
        [switch]$IncludeServices
    )

    begin {
        Write-Verbose "Starting computer data collection"
    }

    process {
        $computer = Get-WmiObject -Class Win32_ComputerSystem -ComputerName $ComputerName
        if ($IncludeServices) {
            $services = Get-Service -ComputerName $ComputerName
        }
    }

    end {
        Write-Verbose "Completed data collection"
    }
}

# Function with pipeline support
function Set-CustomProperty {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline=$true)]
        [PSObject]$InputObject,

        [string]$PropertyName,
        [string]$PropertyValue
    )

    process {
        $InputObject | Add-Member -NotePropertyName $PropertyName -NotePropertyValue $PropertyValue -PassThru
    }
}
`;

      const result = await parserManager.parseFile('test.ps1', powershellCode);
      const extractor = new PowerShellExtractor('powershell', 'test.ps1', powershellCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract functions
      const functions = symbols.filter(s => s.kind === SymbolKind.Function);
      expect(functions.length).toBeGreaterThanOrEqual(3);

      const getUserInfo = functions.find(f => f.name === 'Get-UserInfo');
      expect(getUserInfo).toBeDefined();
      expect(getUserInfo?.signature).toContain('Get-UserInfo');
      expect(getUserInfo?.visibility).toBe('public');

      const getComputerData = functions.find(f => f.name === 'Get-ComputerData');
      expect(getComputerData).toBeDefined();
      expect(getComputerData?.signature).toContain('[CmdletBinding()]');

      const setCustomProperty = functions.find(f => f.name === 'Set-CustomProperty');
      expect(setCustomProperty).toBeDefined();

      // Should extract parameters
      const parameters = symbols.filter(s => s.kind === SymbolKind.Variable && s.parentId);
      expect(parameters.length).toBeGreaterThanOrEqual(4);

      const computerNameParam = parameters.find(p => p.name === 'ComputerName');
      expect(computerNameParam).toBeDefined();
      expect(computerNameParam?.signature).toContain('[Parameter(Mandatory=$true)]');
    });
  });

  describe('Variables and Automatic Variables', () => {
    it('should extract PowerShell variables and automatic variables', async () => {
      const powershellCode = `
# User-defined variables
$Global:ConfigPath = "C:\\Config\\app.config"
$Script:LogLevel = "Debug"
$Local:TempData = @{}

# Variables with different scopes
$env:POWERSHELL_TELEMETRY_OPTOUT = 1
$using:RemoteVariable = $LocalValue

# Complex variable assignments
$Services = Get-Service | Where-Object { $_.Status -eq 'Running' }
$HashTable = @{
    Name = "Test"
    Value = 42
    Active = $true
}

# Array and string manipulation
$Array = @("Item1", "Item2", "Item3")
$ComputerName = $env:COMPUTERNAME
$ProcessList = Get-Process -Name "powershell*"

# Automatic variables usage
Write-Host "PowerShell version: $($PSVersionTable.PSVersion)"
Write-Host "Current location: $PWD"
Write-Host "Last exit code: $LASTEXITCODE"
`;

      const result = await parserManager.parseFile('variables.ps1', powershellCode);
      const extractor = new PowerShellExtractor('powershell', 'variables.ps1', powershellCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract user-defined variables
      const variables = symbols.filter(s => s.kind === SymbolKind.Variable);
      expect(variables.length).toBeGreaterThanOrEqual(6);

      const configPath = variables.find(v => v.name === 'ConfigPath');
      expect(configPath).toBeDefined();
      expect(configPath?.signature).toContain('$Global:ConfigPath');
      expect(configPath?.visibility).toBe('public'); // Global scope

      const logLevel = variables.find(v => v.name === 'LogLevel');
      expect(logLevel).toBeDefined();
      expect(logLevel?.signature).toContain('$Script:LogLevel');

      // Should extract environment variables
      const envVars = variables.filter(v => v.name.includes('env:') || v.signature?.includes('$env:'));
      expect(envVars.length).toBeGreaterThanOrEqual(1);

      // Should extract automatic variables
      const autoVars = variables.filter(v =>
        ['PSVersionTable', 'PWD', 'LASTEXITCODE', 'COMPUTERNAME'].includes(v.name)
      );
      expect(autoVars.length).toBeGreaterThanOrEqual(2);
    });
  });

  describe('Classes and Methods', () => {
    it('should extract PowerShell classes and their methods', async () => {
      const powershellCode = `
# PowerShell class definition
class ComputerInfo {
    [string]$Name
    [string]$OS
    [datetime]$LastBoot
    hidden [string]$InternalId

    # Constructor
    ComputerInfo([string]$computerName) {
        $this.Name = $computerName
        $this.OS = (Get-WmiObject Win32_OperatingSystem).Caption
        $this.LastBoot = (Get-WmiObject Win32_OperatingSystem).LastBootUpTime
        $this.InternalId = [System.Guid]::NewGuid().ToString()
    }

    # Instance method
    [string] GetUptime() {
        $uptime = (Get-Date) - $this.LastBoot
        return "$($uptime.Days) days, $($uptime.Hours) hours"
    }

    # Static method
    static [ComputerInfo] GetLocalComputer() {
        return [ComputerInfo]::new($env:COMPUTERNAME)
    }

    # Method with parameters
    [void] UpdateOS([string]$newOS) {
        $this.OS = $newOS
        Write-Verbose "OS updated to: $newOS"
    }
}

# Enum definition
enum LogLevel {
    Error = 1
    Warning = 2
    Information = 3
    Debug = 4
}

# Class inheritance
class ServerInfo : ComputerInfo {
    [string]$Role
    [int]$Port

    ServerInfo([string]$name, [string]$role, [int]$port) : base($name) {
        $this.Role = $role
        $this.Port = $port
    }

    [string] GetServiceInfo() {
        return "Server: $($this.Name), Role: $($this.Role), Port: $($this.Port)"
    }
}
`;

      const result = await parserManager.parseFile('classes.ps1', powershellCode);
      const extractor = new PowerShellExtractor('powershell', 'classes.ps1', powershellCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract classes
      const classes = symbols.filter(s => s.kind === SymbolKind.Class);
      expect(classes.length).toBeGreaterThanOrEqual(2);

      const computerInfo = classes.find(c => c.name === 'ComputerInfo');
      expect(computerInfo).toBeDefined();
      expect(computerInfo?.visibility).toBe('public');

      const serverInfo = classes.find(c => c.name === 'ServerInfo');
      expect(serverInfo).toBeDefined();

      // Should extract methods
      const methods = symbols.filter(s => s.kind === SymbolKind.Method);
      expect(methods.length).toBeGreaterThanOrEqual(4);

      const getUptime = methods.find(m => m.name === 'GetUptime');
      expect(getUptime).toBeDefined();
      expect(getUptime?.signature).toContain('[string] GetUptime()');

      const getLocalComputer = methods.find(m => m.name === 'GetLocalComputer');
      expect(getLocalComputer).toBeDefined();
      expect(getLocalComputer?.signature).toContain('static');

      // Should extract properties
      const properties = symbols.filter(s => s.kind === SymbolKind.Property);
      expect(properties.length).toBeGreaterThanOrEqual(5);

      const nameProperty = properties.find(p => p.name === 'Name');
      expect(nameProperty).toBeDefined();
      expect(nameProperty?.signature).toContain('[string]$Name');

      const hiddenProperty = properties.find(p => p.name === 'InternalId');
      expect(hiddenProperty).toBeDefined();
      expect(hiddenProperty?.visibility).toBe('private'); // hidden

      // Should extract enums
      const enums = symbols.filter(s => s.kind === SymbolKind.Enum);
      expect(enums.length).toBeGreaterThanOrEqual(1);

      const logLevel = enums.find(e => e.name === 'LogLevel');
      expect(logLevel).toBeDefined();
    });
  });

  describe('Azure and Windows DevOps Commands', () => {
    it('should extract Azure and Windows-specific DevOps commands', async () => {
      const powershellCode = `
# Azure PowerShell commands
function Deploy-AzureResources {
    param($ResourceGroupName, $SubscriptionId)

    # Azure authentication and context
    Connect-AzAccount -SubscriptionId $SubscriptionId
    Set-AzContext -SubscriptionId $SubscriptionId

    # Resource deployment
    New-AzResourceGroup -Name $ResourceGroupName -Location "East US"
    New-AzResourceGroupDeployment -ResourceGroupName $ResourceGroupName -TemplateFile "template.json"

    # Azure Container Instances
    New-AzContainerGroup -ResourceGroupName $ResourceGroupName -Name "myapp-container"

    # Azure Kubernetes Service
    New-AzAksCluster -ResourceGroupName $ResourceGroupName -Name "myapp-aks"
    Get-AzAksCluster | kubectl config use-context
}

# Windows Server management
function Configure-WindowsServer {
    # Windows Features
    Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole
    Install-WindowsFeature -Name Web-Server -IncludeManagementTools

    # Registry operations
    Set-ItemProperty -Path "HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion" -Name "CustomSetting" -Value "Configured"

    # Service management
    Set-Service -Name "W3SVC" -StartupType Automatic
    Start-Service -Name "W3SVC"

    # File and fnewer operations
    New-Item -Path "C:\\inetpub\\wwwroot\\api" -ItemType Directory -Force
    Copy-Item -Path "app\\*" -Destination "C:\\inetpub\\wwwroot\\api" -Recurse

    # PowerShell DSC
    Configuration WebServerConfig {
        Node "localhost" {
            WindowsFeature IIS {
                Ensure = "Present"
                Name = "Web-Server"
            }
        }
    }
}

# DevOps pipeline commands
function Run-DeploymentPipeline {
    # Docker operations
    docker build -t myapp:latest .
    docker push myregistry.azurecr.io/myapp:latest

    # Kubernetes deployments
    kubectl apply -f k8s/deployment.yaml
    kubectl rollout status deployment/myapp

    # Azure CLI operations
    az login --service-principal --username $env:AZURE_CLIENT_ID --password $env:AZURE_CLIENT_SECRET --tenant $env:AZURE_TENANT_ID
    az aks get-credentials --resource-group $ResourceGroupName --name $ClusterName

    # PowerShell remoting
    Invoke-Command -ComputerName $ServerList -ScriptBlock {
        Get-Service | Where-Object { $_.Status -eq 'Stopped' }
    }
}
`;

      const result = await parserManager.parseFile('azure-devops.ps1', powershellCode);
      const extractor = new PowerShellExtractor('powershell', 'azure-devops.ps1', powershellCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract Azure commands
      const azureCommands = symbols.filter(s =>
        s.kind === SymbolKind.Function &&
        (s.name?.startsWith('Connect-Az') || s.name?.startsWith('New-Az') || s.name?.startsWith('Set-Az'))
      );
      expect(azureCommands.length).toBeGreaterThanOrEqual(4);

      const connectAz = azureCommands.find(c => c.name === 'Connect-AzAccount');
      expect(connectAz).toBeDefined();
      expect(connectAz?.docComment).toContain('[Azure CLI Call]');

      // Should extract Windows management commands
      const windowsCommands = symbols.filter(s =>
        s.kind === SymbolKind.Function &&
        (s.name?.includes('Windows') || s.name?.includes('Service') || s.name?.includes('Registry'))
      );
      expect(windowsCommands.length).toBeGreaterThanOrEqual(3);

      // Should extract cross-platform DevOps commands
      const devopsCommands = symbols.filter(s =>
        s.kind === SymbolKind.Function &&
        ['docker', 'kubectl', 'az'].includes(s.name)
      );
      expect(devopsCommands.length).toBeGreaterThanOrEqual(3);

      const dockerCmd = devopsCommands.find(c => c.name === 'docker');
      expect(dockerCmd).toBeDefined();
      expect(dockerCmd?.docComment).toContain('[Docker Container Call]');

      const kubectlCmd = devopsCommands.find(c => c.name === 'kubectl');
      expect(kubectlCmd).toBeDefined();
      expect(kubectlCmd?.docComment).toContain('[Kubernetes CLI Call]');
    });
  });

  describe('Modules and Imports', () => {
    it('should extract PowerShell modules and import statements', async () => {
      const powershellCode = `
# Module imports
Import-Module Az.Accounts
Import-Module Az.Resources -Force
Import-Module -Name "Custom.Tools" -RequiredVersion "2.1.0"

# Dot sourcing
. "$PSScriptRoot\\CommonFunctions.ps1"
. "C:\\Scripts\\HelperFunctions.ps1"

# Using statements (PowerShell 5.0+)
using namespace System.Collections.Generic
using module Az.Storage

# Module manifest variables
$ModuleManifestData = @{
    RootModule = 'MyModule.psm1'
    ModuleVersion = '1.0.0'
    GUID = [System.Guid]::NewGuid()
    Author = 'DevOps Team'
    CompanyName = 'MyCompany'
    PowerShellVersion = '5.1'
    RequiredModules = @('Az.Accounts', 'Az.Resources')
}

# Export module members
Export-ModuleMember -Function Get-CustomData
Export-ModuleMember -Variable ConfigSettings
Export-ModuleMember -Alias gcd
`;

      const result = await parserManager.parseFile('modules.ps1', powershellCode);
      const extractor = new PowerShellExtractor('powershell', 'modules.ps1', powershellCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract import statements
      const imports = symbols.filter(s => s.kind === SymbolKind.Import);
      expect(imports.length).toBeGreaterThanOrEqual(4);

      const azAccounts = imports.find(i => i.name === 'Az.Accounts');
      expect(azAccounts).toBeDefined();
      expect(azAccounts?.signature).toContain('Import-Module Az.Accounts');

      const customTools = imports.find(i => i.name === 'Custom.Tools');
      expect(customTools).toBeDefined();
      expect(customTools?.signature).toContain('RequiredVersion "2.1.0"');

      // Should extract using statements
      const usingStatements = imports.filter(i => i.signature?.includes('using'));
      expect(usingStatements.length).toBeGreaterThanOrEqual(2);

      // Should extract dot sourcing
      const dotSourcing = imports.filter(i => i.signature?.includes('. '));
      expect(dotSourcing.length).toBeGreaterThanOrEqual(2);

      // Should extract export statements
      const exports = symbols.filter(s => s.kind === SymbolKind.Export);
      expect(exports.length).toBeGreaterThanOrEqual(3);
    });
  });

  describe('Error Handling and Edge Cases', () => {
    it('should handle malformed PowerShell and extraction errors gracefully', async () => {
      const malformedPowerShell = `
# Incomplete function
function Incomplete-Function {
    param($Parameter
    # Missing closing brace and parameter definition

# Incomplete class
class Broken-Class {
    [string]$Property
    # Missing closing brace

# Invalid syntax
if ($condition -eq {
    Write-Output "incomplete if statement"

# But should still extract what it can
function Working-Function {
    param([string]$Name)
    Write-Output "Hello, $Name"
}

$ValidVariable = "This should work"
`;

      const result = await parserManager.parseFile('malformed.ps1', malformedPowerShell);
      const extractor = new PowerShellExtractor('powershell', 'malformed.ps1', malformedPowerShell);

      // Should not throw errors
      expect(() => {
        const symbols = extractor.extractSymbols(result.tree);
        const relationships = extractor.extractRelationships(result.tree, symbols);
      }).not.toThrow();

      const symbols = extractor.extractSymbols(result.tree);

      // Should still extract valid symbols
      const validFunction = symbols.find(s => s.name === 'Working-Function');
      expect(validFunction).toBeDefined();

      const validVariable = symbols.find(s => s.name === 'ValidVariable');
      expect(validVariable).toBeDefined();
    });

    it('should handle empty files and minimal content', async () => {
      const emptyPowerShell = '';
      const minimalPowerShell = '# Just a comment\n';

      const emptyResult = await parserManager.parseFile('empty.ps1', emptyPowerShell);
      const minimalResult = await parserManager.parseFile('minimal.ps1', minimalPowerShell);

      const emptyExtractor = new PowerShellExtractor('powershell', 'empty.ps1', emptyPowerShell);
      const minimalExtractor = new PowerShellExtractor('powershell', 'minimal.ps1', minimalPowerShell);

      const emptySymbols = emptyExtractor.extractSymbols(emptyResult.tree);
      const minimalSymbols = minimalExtractor.extractSymbols(minimalResult.tree);

      // Should handle gracefully without errors
      expect(emptySymbols).toBeInstanceOf(Array);
      expect(minimalSymbols).toBeInstanceOf(Array);
      expect(emptySymbols.length).toBe(0);
      expect(minimalSymbols.length).toBe(0);
    });
  });
});