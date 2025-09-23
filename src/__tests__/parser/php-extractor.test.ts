import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { PhpExtractor } from '../../extractors/php-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('PhpExtractor', () => {
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

  describe('Classes and Interfaces', () => {
    it('should extract classes, interfaces, and their members', async () => {
      const phpCode = `<?php

namespace App\\Models;

use App\\Contracts\\UserRepositoryInterface;
use Illuminate\\Database\\Eloquent\\Model;
use Illuminate\\Support\\Facades\\Hash;

interface Drawable
{
    public function draw(): void;
    public function getColor(): string;
    public function setColor(string $color): self;
}

interface Serializable
{
    public function serialize(): string;
    public function unserialize(string $data): void;
}

abstract class Shape implements Drawable
{
    protected string $color = 'black';
    protected static int $instanceCount = 0;

    public const DEFAULT_COLOR = 'white';
    private const MAX_SIZE = 1000;

    public function __construct(string $color = self::DEFAULT_COLOR)
    {
        $this->color = $color;
        self::$instanceCount++;
    }

    public function getColor(): string
    {
        return $this->color;
    }

    public function setColor(string $color): self
    {
        $this->color = $color;
        return $this;
    }

    abstract public function getArea(): float;

    public static function getInstanceCount(): int
    {
        return self::$instanceCount;
    }

    public function __toString(): string
    {
        return "Shape with color: {$this->color}";
    }

    public function __destruct()
    {
        self::$instanceCount--;
    }
}

class Circle extends Shape implements Serializable
{
    private float $radius;

    public function __construct(float $radius, string $color = parent::DEFAULT_COLOR)
    {
        parent::__construct($color);
        $this->radius = $radius;
    }

    public function draw(): void
    {
        echo "Drawing a circle with radius {$this->radius}\\n";
    }

    public function getArea(): float
    {
        return pi() * $this->radius ** 2;
    }

    public function getRadius(): float
    {
        return $this->radius;
    }

    public function setRadius(float $radius): void
    {
        $this->radius = $radius;
    }

    public function serialize(): string
    {
        return json_encode([
            'radius' => $this->radius,
            'color' => $this->color
        ]);
    }

    public function unserialize(string $data): void
    {
        $decoded = json_decode($data, true);
        $this->radius = $decoded['radius'];
        $this->color = $decoded['color'];
    }
}

final class Rectangle extends Shape
{
    private float $width;
    private float $height;

    public function __construct(float $width, float $height, string $color = 'blue')
    {
        parent::__construct($color);
        $this->width = $width;
        $this->height = $height;
    }

    public function draw(): void
    {
        echo "Drawing a rectangle {$this->width}x{$this->height}\\n";
    }

    public function getArea(): float
    {
        return $this->width * $this->height;
    }

    final public function getDimensions(): array
    {
        return [$this->width, $this->height];
    }
}
`;

      const result = await parserManager.parseFile('test.php', phpCode);
      const extractor = new PhpExtractor('php', 'test.php', phpCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Namespace
      const namespace = symbols.find(s => s.name === 'App\\Models');
      expect(namespace).toBeDefined();
      expect(namespace?.kind).toBe(SymbolKind.Namespace);

      // Use statements
      const useStatement = symbols.find(s => s.name === 'App\\Contracts\\UserRepositoryInterface');
      expect(useStatement).toBeDefined();
      expect(useStatement?.kind).toBe(SymbolKind.Import);

      // Interface
      const drawable = symbols.find(s => s.name === 'Drawable');
      expect(drawable).toBeDefined();
      expect(drawable?.kind).toBe(SymbolKind.Interface);

      // Interface methods
      const draw = symbols.find(s => s.name === 'draw' && s.parentId === drawable?.id);
      expect(draw).toBeDefined();
      expect(draw?.signature).toContain('public function draw(): void');

      const getColor = symbols.find(s => s.name === 'getColor' && s.parentId === drawable?.id);
      expect(getColor).toBeDefined();
      expect(getColor?.signature).toContain('getColor(): string');

      const setColor = symbols.find(s => s.name === 'setColor' && s.parentId === drawable?.id);
      expect(setColor).toBeDefined();
      expect(setColor?.signature).toContain('setColor(string $color): self');

      // Abstract class
      const shape = symbols.find(s => s.name === 'Shape');
      expect(shape).toBeDefined();
      expect(shape?.signature).toContain('abstract class Shape implements Drawable');

      // Properties with visibility and types
      const color = symbols.find(s => s.name === 'color');
      expect(color).toBeDefined();
      expect(color?.kind).toBe(SymbolKind.Property);
      expect(color?.visibility).toBe('protected');
      expect(color?.signature).toContain('protected string $color = \'black\'');

      const instanceCount = symbols.find(s => s.name === 'instanceCount');
      expect(instanceCount).toBeDefined();
      expect(instanceCount?.signature).toContain('protected static int $instanceCount = 0');

      // Constants
      const defaultColor = symbols.find(s => s.name === 'DEFAULT_COLOR');
      expect(defaultColor).toBeDefined();
      expect(defaultColor?.kind).toBe(SymbolKind.Constant);
      expect(defaultColor?.visibility).toBe('public');
      expect(defaultColor?.signature).toContain('public const DEFAULT_COLOR = \'white\'');

      const maxSize = symbols.find(s => s.name === 'MAX_SIZE');
      expect(maxSize).toBeDefined();
      expect(maxSize?.visibility).toBe('private');

      // Constructor with parameters and default values
      const constructor = symbols.find(s => s.name === '__construct' && s.parentId === shape?.id);
      expect(constructor).toBeDefined();
      expect(constructor?.kind).toBe(SymbolKind.Constructor);
      expect(constructor?.signature).toContain('__construct(string $color = self::DEFAULT_COLOR)');

      // Abstract method
      const getArea = symbols.find(s => s.name === 'getArea' && s.parentId === shape?.id);
      expect(getArea).toBeDefined();
      expect(getArea?.signature).toContain('abstract public function getArea(): float');

      // Static method
      const getInstanceCount = symbols.find(s => s.name === 'getInstanceCount');
      expect(getInstanceCount).toBeDefined();
      expect(getInstanceCount?.signature).toContain('public static function getInstanceCount(): int');

      // Magic methods
      const toString = symbols.find(s => s.name === '__toString');
      expect(toString).toBeDefined();
      expect(toString?.signature).toContain('public function __toString(): string');

      const destructor = symbols.find(s => s.name === '__destruct');
      expect(destructor).toBeDefined();
      expect(destructor?.kind).toBe(SymbolKind.Destructor);

      // Concrete class with multiple interfaces
      const circle = symbols.find(s => s.name === 'Circle');
      expect(circle).toBeDefined();
      expect(circle?.signature).toContain('class Circle extends Shape implements Serializable');

      // Method with void return type
      const circleDraw = symbols.find(s => s.name === 'draw' && s.parentId === circle?.id);
      expect(circleDraw).toBeDefined();
      expect(circleDraw?.signature).toContain('public function draw(): void');

      // Final class
      const rectangle = symbols.find(s => s.name === 'Rectangle');
      expect(rectangle).toBeDefined();
      expect(rectangle?.signature).toContain('final class Rectangle extends Shape');

      // Final method
      const getDimensions = symbols.find(s => s.name === 'getDimensions');
      expect(getDimensions).toBeDefined();
      expect(getDimensions?.signature).toContain('final public function getDimensions(): array');
    });
  });

  describe('Traits and Modern PHP Features', () => {
    it('should extract traits, enums, and modern PHP 8+ features', async () => {
      const phpCode = `<?php

namespace App\\Enums;

use BackedEnum;
use JsonSerializable;

enum Status: string implements JsonSerializable
{
    case PENDING = 'pending';
    case APPROVED = 'approved';
    case REJECTED = 'rejected';

    public function getLabel(): string
    {
        return match($this) {
            self::PENDING => 'Pending Review',
            self::APPROVED => 'Approved',
            self::REJECTED => 'Rejected',
        };
    }

    public function jsonSerialize(): mixed
    {
        return $this->value;
    }
}

enum Priority: int
{
    case LOW = 1;
    case MEDIUM = 2;
    case HIGH = 3;
    case CRITICAL = 4;

    public function getColor(): string
    {
        return match($this) {
            self::LOW => 'green',
            self::MEDIUM => 'yellow',
            self::HIGH => 'orange',
            self::CRITICAL => 'red',
        };
    }
}

trait Timestampable
{
    protected ?\\DateTime $createdAt = null;
    protected ?\\DateTime $updatedAt = null;

    public function touch(): void
    {
        $this->updatedAt = new \\DateTime();
    }

    public function getCreatedAt(): ?\\DateTime
    {
        return $this->createdAt;
    }

    public function setCreatedAt(\\DateTime $createdAt): self
    {
        $this->createdAt = $createdAt;
        return $this;
    }
}

trait Cacheable
{
    private static array $cache = [];

    public function getCacheKey(): string
    {
        return static::class . ':' . $this->getId();
    }

    public function cache(): void
    {
        self::$cache[$this->getCacheKey()] = $this;
    }

    public static function getFromCache(string $key): ?static
    {
        return self::$cache[$key] ?? null;
    }

    abstract public function getId(): int|string;
}

#[Attribute(Attribute::TARGET_CLASS | Attribute::TARGET_METHOD)]
class ApiResource
{
    public function __construct(
        public readonly string $version = 'v1',
        public readonly bool $deprecated = false,
        public readonly array $scopes = []
    ) {}
}

#[Attribute(Attribute::TARGET_PROPERTY)]
class Validate
{
    public function __construct(
        public readonly array $rules = [],
        public readonly ?string $message = null
    ) {}
}

#[ApiResource(version: 'v2', scopes: ['read', 'write'])]
class User
{
    use Timestampable;
    use Cacheable;

    private const DEFAULT_ROLE = 'user';

    #[Validate(rules: ['required', 'string', 'max:255'])]
    private string $name;

    #[Validate(rules: ['required', 'email', 'unique:users'])]
    private string $email;

    private ?string $password = null;
    private Status $status = Status::PENDING;
    private Priority $priority = Priority::LOW;

    public function __construct(
        string $name,
        string $email,
        ?string $password = null,
        private readonly int $id = 0
    ) {
        $this->name = $name;
        $this->email = $email;
        $this->password = $password ? password_hash($password, PASSWORD_DEFAULT) : null;
        $this->createdAt = new \\DateTime();
        $this->updatedAt = new \\DateTime();
    }

    public function getId(): int
    {
        return $this->id;
    }

    public function getName(): string
    {
        return $this->name;
    }

    public function setName(string $name): void
    {
        $this->name = $name;
        $this->touch();
    }

    #[ApiResource(deprecated: true)]
    public function getEmail(): string
    {
        return $this->email;
    }

    public function updateStatus(Status $status): void
    {
        $this->status = $status;
        $this->touch();
    }

    public function getStatus(): Status
    {
        return $this->status;
    }

    public function hasHighPriority(): bool
    {
        return $this->priority === Priority::HIGH || $this->priority === Priority::CRITICAL;
    }
}

readonly class Configuration
{
    public function __construct(
        public string $database_url,
        public string $api_key,
        public bool $debug_mode = false,
        public array $features = []
    ) {}

    public function isFeatureEnabled(string $feature): bool
    {
        return in_array($feature, $this->features);
    }
}
`;

      const result = await parserManager.parseFile('test.php', phpCode);
      const extractor = new PhpExtractor('php', 'test.php', phpCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Backed enum
      const status = symbols.find(s => s.name === 'Status');
      expect(status).toBeDefined();
      expect(status?.kind).toBe(SymbolKind.Enum);
      expect(status?.signature).toContain('enum Status: string implements JsonSerializable');

      // Enum cases
      const pending = symbols.find(s => s.name === 'PENDING');
      expect(pending).toBeDefined();
      expect(pending?.kind).toBe(SymbolKind.EnumMember);
      expect(pending?.signature).toContain("case PENDING = 'pending'");

      // Enum method
      const getLabel = symbols.find(s => s.name === 'getLabel' && s.parentId === status?.id);
      expect(getLabel).toBeDefined();
      expect(getLabel?.signature).toContain('public function getLabel(): string');

      // Int enum
      const priority = symbols.find(s => s.name === 'Priority');
      expect(priority).toBeDefined();
      expect(priority?.signature).toContain('enum Priority: int');

      const low = symbols.find(s => s.name === 'LOW');
      expect(low).toBeDefined();
      expect(low?.signature).toContain('case LOW = 1');

      // Trait
      const timestampable = symbols.find(s => s.name === 'Timestampable');
      expect(timestampable).toBeDefined();
      expect(timestampable?.kind).toBe(SymbolKind.Trait);

      // Trait properties with nullable types
      const createdAt = symbols.find(s => s.name === 'createdAt');
      expect(createdAt).toBeDefined();
      expect(createdAt?.signature).toContain('protected ?\\DateTime $createdAt = null');

      // Trait method
      const touch = symbols.find(s => s.name === 'touch' && s.parentId === timestampable?.id);
      expect(touch).toBeDefined();

      // Trait with static property
      const cacheable = symbols.find(s => s.name === 'Cacheable');
      expect(cacheable).toBeDefined();

      const cache = symbols.find(s => s.name === 'cache' && s.signature?.includes('private static array'));
      expect(cache).toBeDefined();
      expect(cache?.signature).toContain('private static array $cache = []');

      // Abstract method in trait
      const getId = symbols.find(s => s.name === 'getId' && s.signature?.includes('abstract'));
      expect(getId).toBeDefined();
      expect(getId?.signature).toContain('abstract public function getId(): int|string');

      // Attribute class
      const apiResource = symbols.find(s => s.name === 'ApiResource');
      expect(apiResource).toBeDefined();
      expect(apiResource?.signature).toContain('#[Attribute(Attribute::TARGET_CLASS | Attribute::TARGET_METHOD)]');

      // Constructor property promotion
      const apiConstructor = symbols.find(s => s.name === '__construct' && s.parentId === apiResource?.id);
      expect(apiConstructor).toBeDefined();
      expect(apiConstructor?.signature).toContain('public readonly string $version = \'v1\'');

      // Class with attributes
      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.signature).toContain('#[ApiResource(version: \'v2\', scopes: [\'read\', \'write\'])]');
      expect(user?.signature).toContain('use Timestampable');
      expect(user?.signature).toContain('use Cacheable');

      // Property with attribute
      const name = symbols.find(s => s.name === 'name' && s.parentId === user?.id);
      expect(name).toBeDefined();
      expect(name?.signature).toContain('#[Validate(rules: [\'required\', \'string\', \'max:255\'])]');

      // Union type
      const traitGetId = symbols.find(s => s.name === 'getId' && s.parentId === user?.id);
      expect(traitGetId).toBeDefined();
      expect(traitGetId?.signature).toContain('getId(): int');

      // Method with attribute
      const getEmail = symbols.find(s => s.name === 'getEmail');
      expect(getEmail).toBeDefined();
      expect(getEmail?.signature).toContain('#[ApiResource(deprecated: true)]');

      // Readonly class
      const configuration = symbols.find(s => s.name === 'Configuration');
      expect(configuration).toBeDefined();
      expect(configuration?.signature).toContain('readonly class Configuration');

      // Constructor with property promotion
      const configConstructor = symbols.find(s => s.name === '__construct' && s.parentId === configuration?.id);
      expect(configConstructor).toBeDefined();
      expect(configConstructor?.signature).toContain('public string $database_url');
      expect(configConstructor?.signature).toContain('public bool $debug_mode = false');
    });
  });

  describe('Functions and Closures', () => {
    it('should extract global functions, closures, and anonymous functions', async () => {
      const phpCode = `<?php

function calculateSum(int $a, int $b): int
{
    return $a + $b;
}

function processData(array $data, callable $callback = null): array
{
    if ($callback === null) {
        $callback = fn($item) => $item * 2;
    }

    return array_map($callback, $data);
}

function createMultiplier(int $factor): \\Closure
{
    return function(int $number) use ($factor): int {
        return $number * $factor;
    };
}

function &getReference(array &$array, string $key): mixed
{
    return $array[$key];
}

function formatString(string $template, string ...$args): string
{
    return sprintf($template, ...$args);
}

// Type declarations and union types
function handleValue(int|string|null $value): string
{
    return match(true) {
        is_int($value) => "Integer: $value",
        is_string($value) => "String: $value",
        is_null($value) => "Null value",
    };
}

function processUser(
    string $name,
    int $age,
    ?string $email = null,
    array $options = []
): array {
    return compact('name', 'age', 'email', 'options');
}

// Arrow functions
$numbers = [1, 2, 3, 4, 5];
$doubled = array_map(fn($n) => $n * 2, $numbers);
$filtered = array_filter($numbers, fn($n) => $n > 2);

// Regular closures
$multiplier = function(int $x, int $y): int {
    return $x * $y;
};

$processor = function(array $items) use ($multiplier): array {
    return array_map(fn($item) => $multiplier($item, 2), $items);
};

// Closure with reference capture
$counter = 0;
$incrementer = function() use (&$counter): int {
    return ++$counter;
};

// First-class callable syntax (PHP 8.1+)
$strlen = strlen(...);
$array_map = array_map(...);

class MathOperations
{
    public static function add(int $a, int $b): int
    {
        return $a + $b;
    }

    public function multiply(int $a, int $b): int
    {
        return $a * $b;
    }
}

// Method references
$add = MathOperations::add(...);
$instance = new MathOperations();
$multiply = $instance->multiply(...);

// Anonymous classes
$logger = new class implements \\Psr\\Log\\LoggerInterface {
    public function log($level, $message, array $context = []): void
    {
        echo "[$level] $message\\n";
    }

    public function info($message, array $context = []): void
    {
        $this->log('info', $message, $context);
    }

    // Implement other PSR-3 methods...
    public function emergency($message, array $context = []): void {}
    public function alert($message, array $context = []): void {}
    public function critical($message, array $context = []): void {}
    public function error($message, array $context = []): void {}
    public function warning($message, array $context = []): void {}
    public function notice($message, array $context = []): void {}
    public function debug($message, array $context = []): void {}
};
`;

      const result = await parserManager.parseFile('test.php', phpCode);
      const extractor = new PhpExtractor('php', 'test.php', phpCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Global function with type declarations
      const calculateSum = symbols.find(s => s.name === 'calculateSum');
      expect(calculateSum).toBeDefined();
      expect(calculateSum?.kind).toBe(SymbolKind.Function);
      expect(calculateSum?.signature).toContain('function calculateSum(int $a, int $b): int');

      // Function with callable parameter
      const processData = symbols.find(s => s.name === 'processData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('callable $callback = null');

      // Function returning closure
      const createMultiplier = symbols.find(s => s.name === 'createMultiplier');
      expect(createMultiplier).toBeDefined();
      expect(createMultiplier?.signature).toContain('createMultiplier(int $factor): \\Closure');

      // Function returning reference
      const getReference = symbols.find(s => s.name === 'getReference');
      expect(getReference).toBeDefined();
      expect(getReference?.signature).toContain('function &getReference(array &$array, string $key): mixed');

      // Function with variadic parameters
      const formatString = symbols.find(s => s.name === 'formatString');
      expect(formatString).toBeDefined();
      expect(formatString?.signature).toContain('string ...$args');

      // Function with union types
      const handleValue = symbols.find(s => s.name === 'handleValue');
      expect(handleValue).toBeDefined();
      expect(handleValue?.signature).toContain('int|string|null $value');

      // Function with complex parameters
      const processUser = symbols.find(s => s.name === 'processUser');
      expect(processUser).toBeDefined();
      expect(processUser?.signature).toContain('?string $email = null');
      expect(processUser?.signature).toContain('array $options = []');

      // Variable assignments with closures
      const doubled = symbols.find(s => s.name === 'doubled');
      expect(doubled).toBeDefined();
      expect(doubled?.signature).toContain('array_map(fn($n) => $n * 2, $numbers)');

      const multiplier = symbols.find(s => s.name === 'multiplier');
      expect(multiplier).toBeDefined();
      expect(multiplier?.signature).toContain('function(int $x, int $y): int');

      const processor = symbols.find(s => s.name === 'processor');
      expect(processor).toBeDefined();
      expect(processor?.signature).toContain('use ($multiplier)');

      // Closure with reference capture
      const incrementer = symbols.find(s => s.name === 'incrementer');
      expect(incrementer).toBeDefined();
      expect(incrementer?.signature).toContain('use (&$counter)');

      // First-class callable syntax
      const strlen = symbols.find(s => s.name === 'strlen' && s.signature?.includes('strlen(...)'));
      expect(strlen).toBeDefined();

      // Class for method references
      const mathOperations = symbols.find(s => s.name === 'MathOperations');
      expect(mathOperations).toBeDefined();

      const add = symbols.find(s => s.name === 'add' && s.parentId === mathOperations?.id);
      expect(add).toBeDefined();
      expect(add?.signature).toContain('public static function add(int $a, int $b): int');

      // Method references
      const addRef = symbols.find(s => s.name === 'add' && s.signature?.includes('MathOperations::add(...)'));
      expect(addRef).toBeDefined();

      // Anonymous class
      const logger = symbols.find(s => s.name === 'logger');
      expect(logger).toBeDefined();
      expect(logger?.signature).toContain('new class implements \\Psr\\Log\\LoggerInterface');
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer types from PHP type declarations and doc comments', async () => {
      const phpCode = `<?php

class UserService
{
    public function findById(int $id): ?User
    {
        return User::find($id);
    }

    public function getUsers(): array
    {
        return User::all();
    }

    public function createUser(string $name, string $email): User
    {
        return new User($name, $email);
    }

    public function updateUser(User $user, array $data): bool
    {
        return $user->update($data);
    }

    /**
     * @return User[]
     */
    public function getActiveUsers(): array
    {
        return User::where('active', true)->get();
    }

    /**
     * @param array<string, mixed> $filters
     * @return Collection<User>
     */
    public function searchUsers(array $filters): \\Illuminate\\Support\\Collection
    {
        return User::filter($filters);
    }

    private string $apiKey = 'default-key';
    private ?\\DateTime $lastSync = null;
    private array $cache = [];
}
`;

      const result = await parserManager.parseFile('test.php', phpCode);
      const extractor = new PhpExtractor('php', 'test.php', phpCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      // Function return types
      const findById = symbols.find(s => s.name === 'findById');
      expect(findById).toBeDefined();
      expect(types.get(findById!.id)).toBe('?User');

      const getUsers = symbols.find(s => s.name === 'getUsers');
      expect(getUsers).toBeDefined();
      expect(types.get(getUsers!.id)).toBe('array');

      const createUser = symbols.find(s => s.name === 'createUser');
      expect(createUser).toBeDefined();
      expect(types.get(createUser!.id)).toBe('User');

      const updateUser = symbols.find(s => s.name === 'updateUser');
      expect(updateUser).toBeDefined();
      expect(types.get(updateUser!.id)).toBe('bool');

      // Property types
      const apiKey = symbols.find(s => s.name === 'apiKey');
      expect(apiKey).toBeDefined();
      expect(types.get(apiKey!.id)).toBe('string');

      const lastSync = symbols.find(s => s.name === 'lastSync');
      expect(lastSync).toBeDefined();
      expect(types.get(lastSync!.id)).toBe('?\\DateTime');

      const cache = symbols.find(s => s.name === 'cache');
      expect(cache).toBeDefined();
      expect(types.get(cache!.id)).toBe('array');
    });

    it('should extract inheritance and interface implementation relationships', async () => {
      const phpCode = `<?php

interface Drawable
{
    public function draw(): void;
}

interface Colorable
{
    public function getColor(): string;
    public function setColor(string $color): void;
}

abstract class Shape implements Drawable
{
    protected string $color;

    public function __construct(string $color)
    {
        $this->color = $color;
    }

    public function getColor(): string
    {
        return $this->color;
    }

    abstract public function getArea(): float;
}

class Circle extends Shape implements Colorable
{
    private float $radius;

    public function __construct(float $radius, string $color)
    {
        parent::__construct($color);
        $this->radius = $radius;
    }

    public function draw(): void
    {
        echo "Drawing circle";
    }

    public function setColor(string $color): void
    {
        $this->color = $color;
    }

    public function getArea(): float
    {
        return pi() * $this->radius ** 2;
    }
}

class Rectangle extends Shape implements Colorable
{
    private float $width;
    private float $height;

    public function __construct(float $width, float $height, string $color)
    {
        parent::__construct($color);
        $this->width = $width;
        $this->height = $height;
    }

    public function draw(): void
    {
        echo "Drawing rectangle";
    }

    public function setColor(string $color): void
    {
        $this->color = $color;
    }

    public function getArea(): float
    {
        return $this->width * $this->height;
    }
}
`;

      const result = await parserManager.parseFile('test.php', phpCode);
      const extractor = new PhpExtractor('php', 'test.php', phpCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance and interface implementation relationships
      expect(relationships.length).toBeGreaterThanOrEqual(4);

      // Shape implements Drawable
      const shapeDrawable = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Shape' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Drawable'
      );
      expect(shapeDrawable).toBeDefined();

      // Circle extends Shape
      const circleShape = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Circle' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Shape'
      );
      expect(circleShape).toBeDefined();

      // Circle implements Colorable
      const circleColorable = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Circle' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Colorable'
      );
      expect(circleColorable).toBeDefined();

      // Rectangle extends Shape
      const rectangleShape = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Rectangle' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Shape'
      );
      expect(rectangleShape).toBeDefined();

      // Rectangle implements Colorable
      const rectangleColorable = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Rectangle' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Colorable'
      );
      expect(rectangleColorable).toBeDefined();
    });
  });
});