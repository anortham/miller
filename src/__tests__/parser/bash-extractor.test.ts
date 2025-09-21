import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { BashExtractor } from '../../extractors/bash-extractor.js';
import { SymbolKind, RelationshipKind } from '../../extractors/base-extractor.js';

describe('BashExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Functions and Variables', () => {
    it('should extract bash functions and their parameters', async () => {
      const bashCode = `#!/bin/bash

# Main deployment function
deploy_app() {
    local environment=$1
    local app_name=$2

    echo "Deploying $app_name to $environment"
    build_app "$app_name"
    test_app
}

# Build function
build_app() {
    local name=$1
    npm install
    npm run build
}

test_app() {
    npm test
}

# Environment variables
export NODE_ENV="production"
DATABASE_URL="postgres://localhost:5432/app"
readonly API_KEY="secret123"
declare -r CONFIG_PATH="/etc/app/config"
`;

      const result = await parserManager.parseFile('test.sh', bashCode);
      const extractor = new BashExtractor('bash', 'test.sh', bashCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract functions
      const functions = symbols.filter(s => s.kind === SymbolKind.Function);
      expect(functions.length).toBeGreaterThanOrEqual(3);

      const deployApp = functions.find(f => f.name === 'deploy_app');
      expect(deployApp).toBeDefined();
      expect(deployApp?.signature).toBe('function deploy_app()');
      expect(deployApp?.visibility).toBe('public');

      const buildApp = functions.find(f => f.name === 'build_app');
      expect(buildApp).toBeDefined();

      const testApp = functions.find(f => f.name === 'test_app');
      expect(testApp).toBeDefined();

      // Should extract variables
      const variables = symbols.filter(s => s.kind === SymbolKind.Variable || s.kind === SymbolKind.Constant);
      expect(variables.length).toBeGreaterThanOrEqual(4);

      const nodeEnv = variables.find(v => v.name === 'NODE_ENV');
      expect(nodeEnv).toBeDefined();
      expect(nodeEnv?.visibility).toBe('public'); // exported

      const apiKey = variables.find(v => v.name === 'API_KEY');
      expect(apiKey).toBeDefined();
      expect(apiKey?.kind).toBe(SymbolKind.Constant); // readonly

      // Should extract positional parameters
      const parameters = symbols.filter(s => s.name.startsWith('$') && s.kind === SymbolKind.Variable);
      expect(parameters.length).toBeGreaterThanOrEqual(2);

      const param1 = parameters.find(p => p.name === '$1');
      expect(param1).toBeDefined();
      expect(param1?.signature).toBe('$1 (positional parameter)');
    });
  });

  describe('Cross-Language Command Detection', () => {
    it('should extract DevOps and cross-language command calls', async () => {
      const bashCode = `#!/bin/bash

# DevOps deployment script
setup_environment() {
    # Python application setup
    python3 setup.py install
    pip install -r requirements.txt

    # Node.js service
    npm install
    bun install --production
    node server.js &

    # Go microservice
    go build -o service ./cmd/service
    ./service &

    # Container orchestration
    docker build -t myapp .
    docker-compose up -d
    kubectl apply -f k8s/

    # Infrastructure
    terraform plan
    terraform apply -auto-approve

    # Version control
    git pull origin main
    git push origin feature/new-deploy
}

# Database operations
database_ops() {
    # Java application
    java -jar app.jar migrate
    mvn spring-boot:run &

    # .NET service
    dotnet build
    dotnet run &

    # PHP web service
    php composer.phar install
    php -S localhost:8080 &

    # Ruby service
    bundle install
    ruby app.rb &
}

# Monitoring and tools
monitoring_setup() {
    curl -X POST https://api.service.com/health
    ssh deploy@server "systemctl status myapp"
    scp config.json deploy@server:/etc/myapp/
}
`;

      const result = await parserManager.parseFile('deploy.sh', bashCode);
      const extractor = new BashExtractor('bash', 'deploy.sh', bashCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract cross-language commands
      const commands = symbols.filter(s => s.kind === SymbolKind.Function &&
        ['python3', 'npm', 'bun', 'node', 'go', 'docker', 'kubectl', 'terraform', 'git',
         'java', 'mvn', 'dotnet', 'php', 'ruby', 'curl', 'ssh', 'scp'].includes(s.name));

      expect(commands.length).toBeGreaterThanOrEqual(10);

      // Verify specific commands
      const pythonCmd = commands.find(c => c.name === 'python3');
      expect(pythonCmd).toBeDefined();
      expect(pythonCmd?.docComment).toBe('[Python 3 Interpreter Call]');

      const nodeCmd = commands.find(c => c.name === 'node');
      expect(nodeCmd).toBeDefined();
      expect(nodeCmd?.docComment).toBe('[Node.js Runtime Call]');

      const dockerCmd = commands.find(c => c.name === 'docker');
      expect(dockerCmd).toBeDefined();
      expect(dockerCmd?.docComment).toBe('[Docker Container Call]');

      const kubectlCmd = commands.find(c => c.name === 'kubectl');
      expect(kubectlCmd).toBeDefined();
      expect(kubectlCmd?.docComment).toBe('[Kubernetes CLI Call]');

      const terraformCmd = commands.find(c => c.name === 'terraform');
      expect(terraformCmd).toBeDefined();
      expect(terraformCmd?.docComment).toBe('[Infrastructure as Code Call]');

      const bunCmd = commands.find(c => c.name === 'bun');
      expect(bunCmd).toBeDefined();
      expect(bunCmd?.docComment).toBe('[Bun Runtime Call]');
    });
  });

  describe('Control Flow and Environment Variables', () => {
    it('should extract control flow constructs and environment variables', async () => {
      const bashCode = `#!/bin/bash

# Environment setup
export DOCKER_HOST="tcp://localhost:2376"
export KUBECONFIG="/home/user/.kube/config"
PATH="/usr/local/bin:$PATH"
HOME="/home/deploy"
NODE_ENV="development"

# Conditional deployment
if [ "$NODE_ENV" = "production" ]; then
    echo "Production deployment"
    for service in api frontend worker; do
        echo "Starting $service"
        docker start $service
    done
elif [ "$NODE_ENV" = "staging" ]; then
    echo "Staging deployment"
    while read -r line; do
        echo "Processing: $line"
    done < services.txt
else
    echo "Development environment"
fi

# Function with complex logic
deploy_with_rollback() {
    local deployment_id=$1

    if deploy_service "$deployment_id"; then
        echo "Deployment successful"
        return 0
    else
        echo "Deployment failed, rolling back"
        rollback_service "$deployment_id"
        return 1
    fi
}
`;

      const result = await parserManager.parseFile('control.sh', bashCode);
      const extractor = new BashExtractor('bash', 'control.sh', bashCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract environment variables
      const envVars = symbols.filter(s =>
        (s.kind === SymbolKind.Constant || s.kind === SymbolKind.Variable) &&
        ['DOCKER_HOST', 'KUBECONFIG', 'PATH', 'HOME', 'NODE_ENV'].includes(s.name)
      );
      expect(envVars.length).toBeGreaterThanOrEqual(3);

      const dockerHost = envVars.find(v => v.name === 'DOCKER_HOST');
      expect(dockerHost).toBeDefined();
      expect(dockerHost?.visibility).toBe('public'); // exported
      expect(dockerHost?.docComment).toContain('Environment Variable');

      const kubeConfig = envVars.find(v => v.name === 'KUBECONFIG');
      expect(kubeConfig).toBeDefined();
      expect(kubeConfig?.visibility).toBe('public'); // exported

      // Should extract control flow
      const controlFlow = symbols.filter(s => s.kind === SymbolKind.Method);
      expect(controlFlow.length).toBeGreaterThanOrEqual(2);

      const ifBlock = controlFlow.find(c => c.name.includes('if block'));
      expect(ifBlock).toBeDefined();
      expect(ifBlock?.docComment).toBe('[IF control flow]');

      const forBlock = controlFlow.find(c => c.name.includes('for block'));
      expect(forBlock).toBeDefined();
      expect(forBlock?.docComment).toBe('[FOR control flow]');

      // Should extract functions
      const functions = symbols.filter(s => s.kind === SymbolKind.Function);
      const deployFunc = functions.find(f => f.name === 'deploy_with_rollback');
      expect(deployFunc).toBeDefined();
      expect(deployFunc?.signature).toBe('function deploy_with_rollback()');
    });
  });

  describe('Variable Types and Documentation', () => {
    it('should infer variable types and extract documentation', async () => {
      const bashCode = `#!/bin/bash

# Configuration variables
PORT=8080                    # integer
HOST="localhost"             # string
DEBUG=true                   # boolean
RATE_LIMIT=10.5             # float
CONFIG_PATH="/etc/app"       # path
ARRAY=("item1" "item2")      # array

# Special declarations
declare -i COUNTER=0         # integer declaration
declare -r VERSION="1.0.0"  # readonly string
export -n LOCAL_VAR="test"   # unexported variable
readonly -a SERVICES=("api" "worker")  # readonly array

# Function with local variables
configure_app() {
    local app_name=$1
    local -i retry_count=3
    local -r max_attempts=10

    echo "Configuring $app_name"
}
`;

      const result = await parserManager.parseFile('types.sh', bashCode);
      const extractor = new BashExtractor('bash', 'types.sh', bashCode);
      const types = extractor.inferTypes(extractor.extractSymbols(result.tree));

      // Should infer types correctly
      expect(types.get('PORT')).toBe('integer');
      expect(types.get('HOST')).toBe('string');
      expect(types.get('DEBUG')).toBe('boolean');
      expect(types.get('RATE_LIMIT')).toBe('float');
      expect(types.get('CONFIG_PATH')).toBe('path');

      // Extract symbols to verify declarations
      const symbols = extractor.extractSymbols(result.tree);

      const declarations = symbols.filter(s =>
        ['COUNTER', 'VERSION', 'LOCAL_VAR', 'SERVICES'].includes(s.name)
      );
      expect(declarations.length).toBeGreaterThanOrEqual(3);

      const versionVar = declarations.find(d => d.name === 'VERSION');
      expect(versionVar).toBeDefined();
      expect(versionVar?.kind).toBe(SymbolKind.Constant); // readonly
      expect(versionVar?.docComment).toBe('[READONLY]');

      const counterVar = declarations.find(d => d.name === 'COUNTER');
      expect(counterVar).toBeDefined();
      expect(counterVar?.signature).toBe('declare COUNTER');
    });
  });

  describe('Relationships and Cross-References', () => {
    it('should extract function call relationships', async () => {
      const bashCode = `#!/bin/bash

# Main orchestration function
main() {
    setup_environment
    deploy_services
    verify_deployment
}

# Setup function that calls other functions
setup_environment() {
    install_dependencies
    configure_services
    start_monitoring
}

# Individual service functions
install_dependencies() {
    npm install
    python3 -m pip install -r requirements.txt
}

deploy_services() {
    docker-compose up -d
    kubectl apply -f ./k8s/
}

verify_deployment() {
    curl -f http://localhost:8080/health
    python3 scripts/verify.py
}

# Entry point
main "$@"
`;

      const result = await parserManager.parseFile('orchestrate.sh', bashCode);
      const extractor = new BashExtractor('bash', 'orchestrate.sh', bashCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should extract function call relationships
      const callRelationships = relationships.filter(r => r.kind === RelationshipKind.Calls);
      expect(callRelationships.length).toBeGreaterThanOrEqual(3);

      // Verify specific relationships
      const mainFunction = symbols.find(s => s.name === 'main' && s.kind === SymbolKind.Function);
      const setupFunction = symbols.find(s => s.name === 'setup_environment' && s.kind === SymbolKind.Function);

      expect(mainFunction).toBeDefined();
      expect(setupFunction).toBeDefined();

      // Should have relationship from main to setup_environment
      const mainToSetup = callRelationships.find(r =>
        r.fromSymbolId === mainFunction?.id && r.toSymbolId === setupFunction?.id
      );
      expect(mainToSetup).toBeDefined();

      // Should extract external command calls
      const commands = symbols.filter(s =>
        s.kind === SymbolKind.Function &&
        ['npm', 'python3', 'docker-compose', 'kubectl', 'curl'].includes(s.name)
      );
      expect(commands.length).toBeGreaterThanOrEqual(4);
    });
  });

  describe('Error Handling and Edge Cases', () => {
    it('should handle malformed bash and extraction errors gracefully', async () => {
      const malformedBash = `#!/bin/bash

# Function with minor issues but still parseable
working_function() {
    echo "This should work"
    export VALID_VAR="value"
    # Some undefined variables (not syntax errors)
    echo $UNDEFINED_VAR
}

# Another valid function
helper_function() {
    echo "Helper function"
}
`;

      const result = await parserManager.parseFile('malformed.sh', malformedBash);
      const extractor = new BashExtractor('bash', 'malformed.sh', malformedBash);

      // Should not throw errors
      expect(() => {
        const symbols = extractor.extractSymbols(result.tree);
        const relationships = extractor.extractRelationships(result.tree, symbols);
      }).not.toThrow();

      const symbols = extractor.extractSymbols(result.tree);

      // Should still extract valid symbols
      const validFunction = symbols.find(s => s.name === 'working_function');
      expect(validFunction).toBeDefined();

      const validVar = symbols.find(s => s.name === 'VALID_VAR');
      expect(validVar).toBeDefined();
    });

    it('should handle empty files and minimal content', async () => {
      const emptyBash = '';
      const minimalBash = '#!/bin/bash\n# Just a comment\n';

      const emptyResult = await parserManager.parseFile('empty.sh', emptyBash);
      const minimalResult = await parserManager.parseFile('minimal.sh', minimalBash);

      const emptyExtractor = new BashExtractor('bash', 'empty.sh', emptyBash);
      const minimalExtractor = new BashExtractor('bash', 'minimal.sh', minimalBash);

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