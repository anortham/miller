import { describe, it, expect, beforeAll } from 'bun:test';
import { PythonExtractor } from '../../extractors/python-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('PythonExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Class Extraction', () => {
    it('should extract basic class definitions', async () => {
      const pythonCode = `
class User:
    """A user class for managing user data."""
    pass

class Admin(User):
    def __init__(self):
        super().__init__()
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const userClass = symbols.find(s => s.name === 'User');
      expect(userClass).toBeDefined();
      expect(userClass?.kind).toBe(SymbolKind.Class);
      expect(userClass?.signature).toBe('class User');
      expect(userClass?.docComment).toContain('A user class for managing user data');

      const adminClass = symbols.find(s => s.name === 'Admin');
      expect(adminClass).toBeDefined();
      expect(adminClass?.kind).toBe(SymbolKind.Class);
      expect(adminClass?.signature).toContain('class Admin extends User');
    });

    it('should extract classes with decorators', async () => {
      const pythonCode = `
@dataclass
@final
class Product:
    name: str
    price: float
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const productClass = symbols.find(s => s.name === 'Product');
      expect(productClass).toBeDefined();
      expect(productClass?.signature).toContain('@dataclass @final class Product');
    });
  });

  describe('Function Extraction', () => {
    it('should extract function definitions with type hints', async () => {
      const pythonCode = `
def calculate_tax(amount: float, rate: float = 0.1) -> float:
    """Calculate tax amount."""
    return amount * rate

async def fetch_data(url: str) -> dict:
    """Async function to fetch data."""
    pass
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const calculateTax = symbols.find(s => s.name === 'calculate_tax');
      expect(calculateTax).toBeDefined();
      expect(calculateTax?.kind).toBe(SymbolKind.Function);
      expect(calculateTax?.signature).toContain('def calculate_tax(amount: float, rate: float = 0.1): float');
      expect(calculateTax?.docComment).toBe('Calculate tax amount.');

      const fetchData = symbols.find(s => s.name === 'fetch_data');
      expect(fetchData).toBeDefined();
      expect(fetchData?.kind).toBe(SymbolKind.Function);
      expect(fetchData?.signature).toContain('async def fetch_data(url: str): dict');
      expect(fetchData?.docComment).toBe('Async function to fetch data.');
    });

    it('should extract decorated functions', async () => {
      const pythonCode = `
@staticmethod
@cached
def get_config(key: str) -> str:
    return config[key]

@property
def full_name(self) -> str:
    return f"{self.first_name} {self.last_name}"
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const getConfig = symbols.find(s => s.name === 'get_config');
      expect(getConfig).toBeDefined();
      expect(getConfig?.signature).toContain('@staticmethod @cached def get_config');

      const fullName = symbols.find(s => s.name === 'full_name');
      expect(fullName).toBeDefined();
      expect(fullName?.signature).toContain('@property def full_name');
    });
  });

  describe('Method Extraction', () => {
    it('should extract class methods', async () => {
      const pythonCode = `
class Calculator:
    def __init__(self, precision: int = 2):
        self.precision = precision

    def add(self, a: float, b: float) -> float:
        """Add two numbers."""
        return round(a + b, self.precision)

    def _internal_method(self):
        """Private method."""
        pass

    def __str__(self) -> str:
        return f"Calculator(precision={self.precision})"
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const calculatorClass = symbols.find(s => s.name === 'Calculator');
      expect(calculatorClass).toBeDefined();

      const initMethod = symbols.find(s => s.name === '__init__');
      expect(initMethod).toBeDefined();
      expect(initMethod?.kind).toBe(SymbolKind.Method);
      expect(initMethod?.parentId).toBe(calculatorClass?.id);

      const addMethod = symbols.find(s => s.name === 'add');
      expect(addMethod).toBeDefined();
      expect(addMethod?.kind).toBe(SymbolKind.Method);
      expect(addMethod?.signature).toContain('def add(self, a: float, b: float): float');
      expect(addMethod?.docComment).toBe('Add two numbers.');

      const internalMethod = symbols.find(s => s.name === '_internal_method');
      expect(internalMethod).toBeDefined();
      expect(internalMethod?.visibility).toBe('private');

      const strMethod = symbols.find(s => s.name === '__str__');
      expect(strMethod).toBeDefined();
      expect(strMethod?.visibility).toBe('public'); // Dunder methods are public
    });
  });

  describe('Variable Extraction', () => {
    it('should extract variable assignments with type hints', async () => {
      const pythonCode = `
# Module-level variables
API_URL: str = "https://api.example.com"
MAX_RETRIES = 3
is_debug: bool = False

class Config:
    def __init__(self):
        self.database_url: str = "postgresql://localhost"
        self.timeout = 30
        self._secret_key = "hidden"
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const apiUrl = symbols.find(s => s.name === 'API_URL');
      expect(apiUrl).toBeDefined();
      expect(apiUrl?.kind).toBe(SymbolKind.Constant);
      expect(apiUrl?.signature).toContain(': str = "https://api.example.com"');

      const maxRetries = symbols.find(s => s.name === 'MAX_RETRIES');
      expect(maxRetries).toBeDefined();
      expect(maxRetries?.kind).toBe(SymbolKind.Constant); // Uppercase = constant

      const databaseUrl = symbols.find(s => s.name === 'database_url');
      expect(databaseUrl).toBeDefined();
      expect(databaseUrl?.kind).toBe(SymbolKind.Property); // self.attribute = property
      expect(databaseUrl?.signature).toContain(': str = "postgresql://localhost"');

      const secretKey = symbols.find(s => s.name === '_secret_key');
      expect(secretKey).toBeDefined();
      expect(secretKey?.visibility).toBe('private');
    });
  });

  describe('Import Extraction', () => {
    it('should extract import statements', async () => {
      const pythonCode = `
import os
import json as js
from typing import List, Dict, Optional
from pathlib import Path
from .local_module import LocalClass as LC
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const osImport = symbols.find(s => s.name === 'os');
      expect(osImport).toBeDefined();
      expect(osImport?.kind).toBe(SymbolKind.Import);
      expect(osImport?.signature).toBe('import os');

      const jsonImport = symbols.find(s => s.name === 'js'); // Aliased
      expect(jsonImport).toBeDefined();
      expect(jsonImport?.signature).toBe('import json as js');

      const listImport = symbols.find(s => s.name === 'List');
      expect(listImport).toBeDefined();
      expect(listImport?.signature).toBe('from typing import List');

      const localImport = symbols.find(s => s.name === 'LC'); // Aliased
      expect(localImport).toBeDefined();
      expect(localImport?.signature).toBe('from .local_module import LocalClass as LC');
    });
  });

  describe('Lambda Functions', () => {
    it('should extract lambda functions', async () => {
      const pythonCode = `
numbers = [1, 2, 3, 4, 5]
squared = list(map(lambda x: x ** 2, numbers))
filtered = list(filter(lambda n: n > 3, numbers))
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const lambdas = symbols.filter(s => s.name.startsWith('<lambda:'));
      expect(lambdas.length).toBeGreaterThanOrEqual(2);

      const squareLambda = lambdas.find(s => s.signature?.includes('x ** 2'));
      expect(squareLambda).toBeDefined();
      expect(squareLambda?.kind).toBe(SymbolKind.Function);

      const filterLambda = lambdas.find(s => s.signature?.includes('n > 3'));
      expect(filterLambda).toBeDefined();
    });
  });

  describe('Generators and Yield', () => {
    it('should extract generator functions and coroutines', async () => {
      const pythonCode = `
def fibonacci(n: int):
    """Generate fibonacci sequence."""
    a, b = 0, 1
    for _ in range(n):
        yield a
        a, b = b, a + b

def process_items(items):
    for item in items:
        processed = yield item.upper()
        if processed:
            yield f"Processed: {processed}"

async def async_generator(items):
    """Async generator example."""
    for item in items:
        await asyncio.sleep(0.1)
        yield item * 2

def comprehension_generator():
    return (x**2 for x in range(10) if x % 2 == 0)
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const fibonacci = symbols.find(s => s.name === 'fibonacci');
      expect(fibonacci).toBeDefined();
      expect(fibonacci?.kind).toBe(SymbolKind.Function);
      expect(fibonacci?.signature).toContain('def fibonacci(n: int)');
      expect(fibonacci?.docComment).toBe('Generate fibonacci sequence.');

      const processItems = symbols.find(s => s.name === 'process_items');
      expect(processItems).toBeDefined();
      expect(processItems?.signature).toContain('def process_items(items)');

      const asyncGenerator = symbols.find(s => s.name === 'async_generator');
      expect(asyncGenerator).toBeDefined();
      expect(asyncGenerator?.signature).toContain('async def async_generator(items)');
      expect(asyncGenerator?.docComment).toBe('Async generator example.');

      const comprehensionGen = symbols.find(s => s.name === 'comprehension_generator');
      expect(comprehensionGen).toBeDefined();
    });
  });

  describe('Context Managers and Exception Handling', () => {
    it('should extract context managers and exception classes', async () => {
      const pythonCode = `
class DatabaseError(Exception):
    """Custom database exception."""
    def __init__(self, message: str, code: int = None):
        super().__init__(message)
        self.code = code

class ConnectionManager:
    """Database connection context manager."""
    def __init__(self, url: str):
        self.url = url
        self.connection = None

    def __enter__(self):
        self.connection = connect(self.url)
        return self.connection

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.connection:
            self.connection.close()
        if exc_type is DatabaseError:
            print(f"Database error occurred: {exc_val}")
            return True  # Suppress exception
        return False

def safe_operation():
    try:
        risky_operation()
    except (ValueError, TypeError) as e:
        logger.error(f"Error: {e}")
        raise DatabaseError("Operation failed") from e
    except DatabaseError:
        logger.critical("Database error occurred")
        raise
    else:
        logger.info("Operation successful")
    finally:
        cleanup_resources()
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const dbError = symbols.find(s => s.name === 'DatabaseError');
      expect(dbError).toBeDefined();
      expect(dbError?.kind).toBe(SymbolKind.Class);
      expect(dbError?.signature).toContain('class DatabaseError extends Exception');
      expect(dbError?.docComment).toBe('Custom database exception.');

      const connManager = symbols.find(s => s.name === 'ConnectionManager');
      expect(connManager).toBeDefined();
      expect(connManager?.docComment).toBe('Database connection context manager.');

      const enterMethod = symbols.find(s => s.name === '__enter__');
      expect(enterMethod).toBeDefined();
      expect(enterMethod?.kind).toBe(SymbolKind.Method);

      const exitMethod = symbols.find(s => s.name === '__exit__');
      expect(exitMethod).toBeDefined();
      expect(exitMethod?.kind).toBe(SymbolKind.Method);

      const safeOp = symbols.find(s => s.name === 'safe_operation');
      expect(safeOp).toBeDefined();
      expect(safeOp?.kind).toBe(SymbolKind.Function);
    });
  });

  describe('Advanced Decorators and Metaclasses', () => {
    it('should extract advanced decorator patterns and metaclasses', async () => {
      const pythonCode = `
from functools import wraps, lru_cache, singledispatch
from abc import ABC, abstractmethod

def timing_decorator(func):
    """Decorator to measure execution time."""
    @wraps(func)
    def wrapper(*args, **kwargs):
        start = time.time()
        result = func(*args, **kwargs)
        end = time.time()
        print(f"{func.__name__} took {end - start:.4f} seconds")
        return result
    return wrapper

class SingletonMeta(type):
    """Singleton metaclass."""
    _instances = {}

    def __call__(cls, *args, **kwargs):
        if cls not in cls._instances:
            cls._instances[cls] = super().__call__(*args, **kwargs)
        return cls._instances[cls]

class Database(metaclass=SingletonMeta):
    """Singleton database connection."""
    def __init__(self, url: str):
        self.url = url
        self.connected = False

@singledispatch
def process_data(data):
    """Generic data processor."""
    raise NotImplementedError(f"Cannot process {type(data)}")

@process_data.register
def _(data: str):
    return data.upper()

@process_data.register
def _(data: int):
    return data * 2

class APIClient(ABC):
    """Abstract API client."""

    @abstractmethod
    def get(self, endpoint: str) -> dict:
        """Get data from endpoint."""
        pass

    @abstractmethod
    def post(self, endpoint: str, data: dict) -> dict:
        """Post data to endpoint."""
        pass

    @classmethod
    def create_client(cls, api_key: str):
        """Factory method for creating clients."""
        return cls(api_key)

class Calculator:
    """Calculator with cached operations."""

    @lru_cache(maxsize=128)
    def fibonacci(self, n: int) -> int:
        """Cached fibonacci calculation."""
        if n <= 1:
            return n
        return self.fibonacci(n-1) + self.fibonacci(n-2)

    @timing_decorator
    def complex_operation(self, data: list) -> float:
        """A complex operation that needs timing."""
        return sum(x**2 for x in data)
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const timingDecorator = symbols.find(s => s.name === 'timing_decorator');
      expect(timingDecorator).toBeDefined();
      expect(timingDecorator?.kind).toBe(SymbolKind.Function);
      expect(timingDecorator?.docComment).toBe('Decorator to measure execution time.');

      const singletonMeta = symbols.find(s => s.name === 'SingletonMeta');
      expect(singletonMeta).toBeDefined();
      expect(singletonMeta?.kind).toBe(SymbolKind.Class);
      expect(singletonMeta?.signature).toContain('class SingletonMeta extends type');

      const database = symbols.find(s => s.name === 'Database');
      expect(database).toBeDefined();
      expect(database?.signature).toContain('class Database metaclass=SingletonMeta');

      const processData = symbols.find(s => s.name === 'process_data');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('@singledispatch def process_data');

      const apiClient = symbols.find(s => s.name === 'APIClient');
      expect(apiClient).toBeDefined();
      expect(apiClient?.signature).toContain('class APIClient extends ABC');

      const getMethod = symbols.find(s => s.name === 'get' && s.parentId === apiClient?.id);
      expect(getMethod).toBeDefined();
      expect(getMethod?.signature).toContain('@abstractmethod def get');

      const fibonacci = symbols.find(s => s.name === 'fibonacci');
      expect(fibonacci).toBeDefined();
      expect(fibonacci?.signature).toContain('@lru_cache def fibonacci');

      const complexOp = symbols.find(s => s.name === 'complex_operation');
      expect(complexOp).toBeDefined();
      expect(complexOp?.signature).toContain('@timing_decorator def complex_operation');
    });
  });

  describe('Modern Python Features', () => {
    it('should extract modern Python syntax and type annotations', async () => {
      const pythonCode = `
from typing import TypeVar, Generic, Protocol, Literal, Union, Final
from dataclasses import dataclass, field
from enum import Enum, auto
from collections.abc import Callable

T = TypeVar('T', bound='Comparable')
NumberType = Union[int, float]
Status = Literal['pending', 'approved', 'rejected']

API_VERSION: Final[str] = "v1.2.3"

class Color(Enum):
    """Color enumeration."""
    RED = auto()
    GREEN = auto()
    BLUE = auto()

    def hex_value(self) -> str:
        colors = {self.RED: "#FF0000", self.GREEN: "#00FF00", self.BLUE: "#0000FF"}
        return colors[self]

class Comparable(Protocol):
    """Protocol for comparable objects."""
    def __lt__(self, other: 'Comparable') -> bool: ...
    def __eq__(self, other: object) -> bool: ...

@dataclass(frozen=True, slots=True)
class Point:
    """Immutable point with slots."""
    x: float
    y: float
    metadata: dict = field(default_factory=dict)

    def distance_from_origin(self) -> float:
        return (self.x ** 2 + self.y ** 2) ** 0.5

class Container(Generic[T]):
    """Generic container class."""
    def __init__(self):
        self._items: list[T] = []

    def add(self, item: T) -> None:
        self._items.append(item)

    def get_all(self) -> list[T]:
        return self._items.copy()

def process_status(status: Status) -> str:
    """Process status using match expression."""
    match status:
        case 'pending':
            return "⏳ Waiting for approval"
        case 'approved':
            return "✅ Request approved"
        case 'rejected':
            return "❌ Request rejected"
        case _:
            return "❓ Unknown status"

def calculate_score(base: int, multiplier: float = 1.0) -> NumberType:
    """Calculate score with walrus operator."""
    if (score := base * multiplier) > 100:
        return min(score, 1000)
    return score

def higher_order_function(func: Callable[[int], int], value: int) -> int:
    """Function that takes another function as parameter."""
    return func(value) * 2
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const tVar = symbols.find(s => s.name === 'T');
      expect(tVar).toBeDefined();
      expect(tVar?.signature).toContain("TypeVar('T', bound='Comparable')");

      const apiVersion = symbols.find(s => s.name === 'API_VERSION');
      expect(apiVersion).toBeDefined();
      expect(apiVersion?.signature).toContain('Final[str] = "v1.2.3"');

      const colorEnum = symbols.find(s => s.name === 'Color');
      expect(colorEnum).toBeDefined();
      expect(colorEnum?.kind).toBe(SymbolKind.Enum);
      expect(colorEnum?.signature).toContain('class Color extends Enum');

      const redValue = symbols.find(s => s.name === 'RED');
      expect(redValue).toBeDefined();
      expect(redValue?.kind).toBe(SymbolKind.EnumMember);

      const comparable = symbols.find(s => s.name === 'Comparable');
      expect(comparable).toBeDefined();
      expect(comparable?.kind).toBe(SymbolKind.Interface); // Protocol = Interface
      expect(comparable?.signature).toContain('class Comparable extends Protocol');

      const point = symbols.find(s => s.name === 'Point');
      expect(point).toBeDefined();
      expect(point?.signature).toContain('@dataclass class Point');
      expect(point?.docComment).toBe('Immutable point with slots.');

      const container = symbols.find(s => s.name === 'Container');
      expect(container).toBeDefined();
      expect(container?.signature).toContain('class Container extends Generic[T]');

      const processStatus = symbols.find(s => s.name === 'process_status');
      expect(processStatus).toBeDefined();
      expect(processStatus?.signature).toContain('def process_status(status: Status): str');

      const calculateScore = symbols.find(s => s.name === 'calculate_score');
      expect(calculateScore).toBeDefined();
      expect(calculateScore?.signature).toContain('def calculate_score(base: int, multiplier: float = 1.0): NumberType');

      const higherOrder = symbols.find(s => s.name === 'higher_order_function');
      expect(higherOrder).toBeDefined();
      expect(higherOrder?.signature).toContain('Callable[[int], int]');
    });
  });

  describe('Property Descriptors and Advanced Class Features', () => {
    it('should extract property descriptors and advanced class patterns', async () => {
      const pythonCode = `
class Descriptor:
    """Custom descriptor class."""
    def __init__(self, name: str):
        self.name = name
        self.value = None

    def __get__(self, obj, objtype=None):
        if obj is None:
            return self
        return getattr(obj, f"_{self.name}", self.value)

    def __set__(self, obj, value):
        setattr(obj, f"_{self.name}", value)

    def __delete__(self, obj):
        delattr(obj, f"_{self.name}")

class Temperature:
    """Temperature class with descriptor."""
    celsius = Descriptor("celsius")

    def __init__(self, celsius: float = 0):
        self.celsius = celsius

    @property
    def fahrenheit(self) -> float:
        """Convert celsius to fahrenheit."""
        return self.celsius * 9/5 + 32

    @fahrenheit.setter
    def fahrenheit(self, value: float):
        """Set temperature from fahrenheit."""
        self.celsius = (value - 32) * 5/9

    @fahrenheit.deleter
    def fahrenheit(self):
        """Reset temperature."""
        self.celsius = 0

class MultipleInheritance(dict, list):
    """Class with multiple inheritance."""
    def __init__(self):
        dict.__init__(self)
        list.__init__(self)
        self._data = {}

    def add_item(self, key: str, value):
        self._data[key] = value
        self.append(value)

class NestedClasses:
    """Class containing nested classes."""

    class InnerClass:
        """Inner class definition."""
        def __init__(self, value: int):
            self.value = value

        def get_double(self) -> int:
            return self.value * 2

    class AnotherInner:
        """Another inner class."""
        CONSTANT = "inner_constant"

        @staticmethod
        def static_method() -> str:
            return "from inner class"

    def __init__(self):
        self.inner = self.InnerClass(42)

class ClassWithSlots:
    """Class using __slots__ for memory optimization."""
    __slots__ = ['x', 'y', '_private']

    def __init__(self, x: int, y: int):
        self.x = x
        self.y = y
        self._private = "secret"

    def __repr__(self) -> str:
        return f"Point({self.x}, {self.y})"
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const descriptor = symbols.find(s => s.name === 'Descriptor');
      expect(descriptor).toBeDefined();
      expect(descriptor?.kind).toBe(SymbolKind.Class);
      expect(descriptor?.docComment).toBe('Custom descriptor class.');

      const getMethod = symbols.find(s => s.name === '__get__');
      expect(getMethod).toBeDefined();
      expect(getMethod?.kind).toBe(SymbolKind.Method);

      const temperature = symbols.find(s => s.name === 'Temperature');
      expect(temperature).toBeDefined();
      expect(temperature?.docComment).toBe('Temperature class with descriptor.');

      const celsiusProperty = symbols.find(s => s.name === 'celsius');
      expect(celsiusProperty).toBeDefined();
      expect(celsiusProperty?.kind).toBe(SymbolKind.Property);

      const fahrenheitProperty = symbols.find(s => s.name === 'fahrenheit');
      expect(fahrenheitProperty).toBeDefined();
      expect(fahrenheitProperty?.signature).toContain('@property def fahrenheit');
      expect(fahrenheitProperty?.docComment).toBe('Convert celsius to fahrenheit.');

      const multipleInheritance = symbols.find(s => s.name === 'MultipleInheritance');
      expect(multipleInheritance).toBeDefined();
      expect(multipleInheritance?.signature).toContain('class MultipleInheritance extends dict, list');

      const nestedClasses = symbols.find(s => s.name === 'NestedClasses');
      expect(nestedClasses).toBeDefined();
      expect(nestedClasses?.docComment).toBe('Class containing nested classes.');

      const innerClass = symbols.find(s => s.name === 'InnerClass');
      expect(innerClass).toBeDefined();
      expect(innerClass?.kind).toBe(SymbolKind.Class);
      expect(innerClass?.docComment).toBe('Inner class definition.');
      expect(innerClass?.parentId).toBe(nestedClasses?.id);

      const anotherInner = symbols.find(s => s.name === 'AnotherInner');
      expect(anotherInner).toBeDefined();
      expect(anotherInner?.parentId).toBe(nestedClasses?.id);

      const innerConstant = symbols.find(s => s.name === 'CONSTANT');
      expect(innerConstant).toBeDefined();
      expect(innerConstant?.kind).toBe(SymbolKind.Constant);

      const classWithSlots = symbols.find(s => s.name === 'ClassWithSlots');
      expect(classWithSlots).toBeDefined();
      expect(classWithSlots?.docComment).toBe('Class using __slots__ for memory optimization.');

      const slotsProperty = symbols.find(s => s.name === '__slots__');
      expect(slotsProperty).toBeDefined();
      expect(slotsProperty?.kind).toBe(SymbolKind.Property);
    });
  });

  describe('Testing Patterns and Mock Objects', () => {
    it('should extract pytest fixtures and testing patterns', async () => {
      const pythonCode = `
import pytest
from unittest.mock import Mock, patch, MagicMock
from typing import Generator

@pytest.fixture
def sample_data() -> dict:
    """Fixture providing sample test data."""
    return {"name": "test", "value": 42}

@pytest.fixture(scope="session")
def database_connection():
    """Session-scoped database fixture."""
    conn = create_test_connection()
    yield conn
    conn.close()

@pytest.mark.parametrize("input,expected", [
    ("hello", "HELLO"),
    ("world", "WORLD"),
    ("", ""),
])
def test_uppercase(input: str, expected: str):
    """Test string uppercase conversion."""
    assert input.upper() == expected

class TestUserService:
    """Test class for user service."""

    def setup_method(self):
        """Setup method called before each test."""
        self.service = UserService()
        self.mock_db = Mock()

    def teardown_method(self):
        """Teardown method called after each test."""
        self.mock_db.reset_mock()

    @patch('user_service.database')
    def test_get_user_success(self, mock_database):
        """Test successful user retrieval."""
        mock_database.get_user.return_value = User(id=1, name="test")
        user = self.service.get_user(1)
        assert user.name == "test"
        mock_database.get_user.assert_called_once_with(1)

    @patch.object(UserService, 'validate_user')
    def test_create_user_validation(self, mock_validate):
        """Test user creation with validation."""
        mock_validate.return_value = True
        user_data = {"name": "new_user", "email": "test@example.com"}
        result = self.service.create_user(user_data)
        assert result is not None
        mock_validate.assert_called_once()

def test_with_context_manager():
    """Test using context manager."""
    with patch('builtins.open', mock_open(read_data="test data")) as mock_file:
        content = read_file("test.txt")
        assert content == "test data"
        mock_file.assert_called_once_with("test.txt", 'r')

@pytest.mark.asyncio
async def test_async_function():
    """Test async function with pytest-asyncio."""
    mock_client = MagicMock()
    mock_client.get.return_value = {"status": "success"}

    result = await async_api_call(mock_client, "/endpoint")
    assert result["status"] == "success"
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      const sampleData = symbols.find(s => s.name === 'sample_data');
      expect(sampleData).toBeDefined();
      expect(sampleData?.signature).toContain('@pytest.fixture def sample_data');
      expect(sampleData?.docComment).toBe('Fixture providing sample test data.');

      const dbConnection = symbols.find(s => s.name === 'database_connection');
      expect(dbConnection).toBeDefined();
      expect(dbConnection?.signature).toContain('@pytest.fixture def database_connection');

      const testUppercase = symbols.find(s => s.name === 'test_uppercase');
      expect(testUppercase).toBeDefined();
      expect(testUppercase?.signature).toContain('@pytest.mark.parametrize def test_uppercase');

      const testClass = symbols.find(s => s.name === 'TestUserService');
      expect(testClass).toBeDefined();
      expect(testClass?.kind).toBe(SymbolKind.Class);
      expect(testClass?.docComment).toBe('Test class for user service.');

      const setupMethod = symbols.find(s => s.name === 'setup_method');
      expect(setupMethod).toBeDefined();
      expect(setupMethod?.kind).toBe(SymbolKind.Method);
      expect(setupMethod?.docComment).toBe('Setup method called before each test.');

      const testGetUser = symbols.find(s => s.name === 'test_get_user_success');
      expect(testGetUser).toBeDefined();
      expect(testGetUser?.signature).toContain('@patch def test_get_user_success');

      const testCreateUser = symbols.find(s => s.name === 'test_create_user_validation');
      expect(testCreateUser).toBeDefined();
      expect(testCreateUser?.signature).toContain('@patch.object def test_create_user_validation');

      const testContextManager = symbols.find(s => s.name === 'test_with_context_manager');
      expect(testContextManager).toBeDefined();
      expect(testContextManager?.docComment).toBe('Test using context manager.');

      const testAsync = symbols.find(s => s.name === 'test_async_function');
      expect(testAsync).toBeDefined();
      expect(testAsync?.signature).toContain('@pytest.mark.asyncio async def test_async_function');
    });
  });

  describe('Complex Python Features', () => {
    it('should handle comprehensive Python code', async () => {
      const pythonCode = `
from typing import List, Optional
from dataclasses import dataclass
import asyncio

@dataclass
class User:
    """A user data class."""
    id: int
    name: str
    email: Optional[str] = None

    def __post_init__(self):
        if self.email is None:
            self.email = f"{self.name.lower()}@example.com"

    @property
    def display_name(self) -> str:
        return self.name.title()

    @staticmethod
    def create_admin(name: str) -> 'User':
        return User(id=0, name=name, email=f"admin-{name}@example.com")

class UserManager:
    def __init__(self):
        self._users: List[User] = []
        self.MAX_USERS = 1000

    async def fetch_user(self, user_id: int) -> Optional[User]:
        """Fetch user by ID asynchronously."""
        await asyncio.sleep(0.1)  # Simulate async operation
        return next((u for u in self._users if u.id == user_id), None)

    def add_user(self, user: User) -> bool:
        if len(self._users) >= self.MAX_USERS:
            return False
        self._users.append(user)
        return True

    def _validate_user(self, user: User) -> bool:
        """Private validation method."""
        return user.name and user.email

# Global configuration
DEBUG = True
users_cache = {}

def process_users(users: List[User]) -> List[str]:
    return [user.display_name for user in users if user.email]
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Check we extracted all major symbols
      expect(symbols.find(s => s.name === 'User')).toBeDefined();
      expect(symbols.find(s => s.name === 'UserManager')).toBeDefined();
      expect(symbols.find(s => s.name === 'display_name')).toBeDefined();
      expect(symbols.find(s => s.name === 'create_admin')).toBeDefined();
      expect(symbols.find(s => s.name === 'fetch_user')).toBeDefined();
      expect(symbols.find(s => s.name === '_validate_user')).toBeDefined();
      expect(symbols.find(s => s.name === 'DEBUG')).toBeDefined();
      expect(symbols.find(s => s.name === 'process_users')).toBeDefined();

      // Check specific features
      const userClass = symbols.find(s => s.name === 'User');
      expect(userClass?.signature).toContain('@dataclass class User');

      const fetchUser = symbols.find(s => s.name === 'fetch_user');
      expect(fetchUser?.signature).toContain('async def fetch_user');

      const validateUser = symbols.find(s => s.name === '_validate_user');
      expect(validateUser?.visibility).toBe('private');

      const debugVar = symbols.find(s => s.name === 'DEBUG');
      expect(debugVar?.kind).toBe(SymbolKind.Constant);

      console.log(`🐍 Extracted ${symbols.length} Python symbols successfully`);
    });
  });

  describe('Comprehensive Relationship Extraction', () => {
    it('should extract inheritance and implementation relationships', async () => {
      const pythonCode = `
from abc import ABC, abstractmethod
from typing import Protocol

class Drawable(Protocol):
    """Protocol for drawable objects."""
    def draw(self) -> None: ...
    def get_area(self) -> float: ...

class Shape(ABC):
    """Abstract base shape class."""
    def __init__(self, color: str):
        self.color = color

    @abstractmethod
    def get_area(self) -> float:
        pass

    def get_perimeter(self) -> float:
        return 0.0

class Circle(Shape, Drawable):
    """Circle implementation."""
    def __init__(self, radius: float, color: str = "black"):
        super().__init__(color)
        self.radius = radius

    def get_area(self) -> float:
        return 3.14159 * self.radius ** 2

    def draw(self) -> None:
        print(f"Drawing {self.color} circle")

class Rectangle(Shape):
    """Rectangle implementation."""
    def __init__(self, width: float, height: float, color: str = "black"):
        super().__init__(color)
        self.width = width
        self.height = height

    def get_area(self) -> float:
        return self.width * self.height

    def get_perimeter(self) -> float:
        return 2 * (self.width + self.height)

class ShapeManager:
    """Manager for shape operations."""
    def __init__(self):
        self.shapes: list[Shape] = []

    def add_shape(self, shape: Shape) -> None:
        self.shapes.append(shape)

    def calculate_total_area(self) -> float:
        return sum(shape.get_area() for shape in self.shapes)

    def draw_all_drawable(self) -> None:
        for shape in self.shapes:
            if isinstance(shape, Drawable):
                shape.draw()
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Find classes
      const drawable = symbols.find(s => s.name === 'Drawable');
      const shape = symbols.find(s => s.name === 'Shape');
      const circle = symbols.find(s => s.name === 'Circle');
      const rectangle = symbols.find(s => s.name === 'Rectangle');

      expect(drawable).toBeDefined();
      expect(shape).toBeDefined();
      expect(circle).toBeDefined();
      expect(rectangle).toBeDefined();

      // Check inheritance relationships
      const shapeExtendsABC = relationships.find(r =>
        r.kind === 'extends' &&
        r.fromSymbolId === shape?.id &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'ABC'
      );
      expect(shapeExtendsABC).toBeDefined();

      const circleExtendsShape = relationships.find(r =>
        r.kind === 'extends' &&
        r.fromSymbolId === circle?.id &&
        r.toSymbolId === shape?.id
      );
      expect(circleExtendsShape).toBeDefined();

      const circleImplementsDrawable = relationships.find(r =>
        r.kind === 'implements' &&
        r.fromSymbolId === circle?.id &&
        r.toSymbolId === drawable?.id
      );
      expect(circleImplementsDrawable).toBeDefined();

      const rectangleExtendsShape = relationships.find(r =>
        r.kind === 'extends' &&
        r.fromSymbolId === rectangle?.id &&
        r.toSymbolId === shape?.id
      );
      expect(rectangleExtendsShape).toBeDefined();

      // Check usage relationships
      const managerUsesShape = relationships.find(r =>
        r.kind === 'uses' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'ShapeManager' &&
        r.toSymbolId === shape?.id
      );
      expect(managerUsesShape).toBeDefined();

      console.log(`🔗 Found ${relationships.length} Python relationships`);
    });

    it('should extract method call relationships', async () => {
      const pythonCode = `
class DatabaseConnection:
    def connect(self) -> bool:
        return True

    def execute_query(self, query: str) -> list:
        return []

    def close(self) -> None:
        pass

class UserRepository:
    def __init__(self, db: DatabaseConnection):
        self.db = db

    def get_user(self, user_id: int):
        self.db.connect()
        try:
            result = self.db.execute_query(f"SELECT * FROM users WHERE id = {user_id}")
            return result[0] if result else None
        finally:
            self.db.close()

    def create_user(self, user_data: dict) -> bool:
        self.db.connect()
        query = "INSERT INTO users (name, email) VALUES (?, ?)"
        self.db.execute_query(query)
        self.db.close()
        return True

def process_user_data(repo: UserRepository, user_id: int):
    user = repo.get_user(user_id)
    if user:
        return user
    return repo.create_user({"name": "default", "email": "default@example.com"})
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      const dbConnection = symbols.find(s => s.name === 'DatabaseConnection');
      const userRepo = symbols.find(s => s.name === 'UserRepository');
      const connectMethod = symbols.find(s => s.name === 'connect');
      const executeMethod = symbols.find(s => s.name === 'execute_query');
      const getUserMethod = symbols.find(s => s.name === 'get_user');

      expect(dbConnection).toBeDefined();
      expect(userRepo).toBeDefined();
      expect(connectMethod).toBeDefined();
      expect(executeMethod).toBeDefined();
      expect(getUserMethod).toBeDefined();

      // Check that UserRepository uses DatabaseConnection
      const repoUsesDb = relationships.find(r =>
        r.kind === 'uses' &&
        r.fromSymbolId === userRepo?.id &&
        r.toSymbolId === dbConnection?.id
      );
      expect(repoUsesDb).toBeDefined();

      // Check method call relationships
      const getUserCallsConnect = relationships.find(r =>
        r.kind === 'calls' &&
        r.fromSymbolId === getUserMethod?.id &&
        r.toSymbolId === connectMethod?.id
      );
      expect(getUserCallsConnect).toBeDefined();

      const getUserCallsExecute = relationships.find(r =>
        r.kind === 'calls' &&
        r.fromSymbolId === getUserMethod?.id &&
        r.toSymbolId === executeMethod?.id
      );
      expect(getUserCallsExecute).toBeDefined();

      console.log(`📞 Found ${relationships.filter(r => r.kind === 'calls').length} call relationships`);
    });
  });

  describe('Type Inference and Advanced Annotations', () => {
    it('should infer types from Python annotations', async () => {
      const pythonCode = `
def get_name() -> str:
    return "test"

def calculate(x: int, y: float) -> float:
    return x + y

username: str = "admin"
count: int = 42
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      const getName = symbols.find(s => s.name === 'get_name');
      expect(getName).toBeDefined();
      expect(types.get(getName!.id)).toBe('str');

      const calculate = symbols.find(s => s.name === 'calculate');
      expect(calculate).toBeDefined();
      expect(types.get(calculate!.id)).toBe('float');

      const username = symbols.find(s => s.name === 'username');
      expect(username).toBeDefined();
      expect(types.get(username!.id)).toBe('str');
    });

    it('should handle complex type annotations and generics', async () => {
      const pythonCode = `
from typing import Dict, List, Optional, Union, Callable, TypeVar, Generic
from collections.abc import Iterable, Mapping

T = TypeVar('T')
K = TypeVar('K')
V = TypeVar('V')

def process_mapping(data: Mapping[str, Union[int, str]]) -> Dict[str, str]:
    """Process a mapping to string values."""
    return {k: str(v) for k, v in data.items()}

def filter_items(items: Iterable[T], predicate: Callable[[T], bool]) -> List[T]:
    """Filter items using a predicate function."""
    return [item for item in items if predicate(item)]

class Cache(Generic[K, V]):
    """Generic cache implementation."""
    def __init__(self, maxsize: int = 128):
        self._cache: Dict[K, V] = {}
        self._maxsize = maxsize

    def get(self, key: K, default: Optional[V] = None) -> Optional[V]:
        return self._cache.get(key, default)

    def set(self, key: K, value: V) -> None:
        if len(self._cache) >= self._maxsize:
            # Remove oldest item
            oldest_key = next(iter(self._cache))
            del self._cache[oldest_key]
        self._cache[key] = value

async def fetch_data(urls: List[str]) -> Dict[str, Optional[str]]:
    """Fetch data from multiple URLs."""
    results = {}
    for url in urls:
        try:
            data = await http_client.get(url)
            results[url] = data.text
        except Exception:
            results[url] = None
    return results

def complex_return_type() -> Union[Dict[str, List[int]], None]:
    """Function with complex return type."""
    return {"numbers": [1, 2, 3]} if random.choice([True, False]) else None
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      const processMapping = symbols.find(s => s.name === 'process_mapping');
      expect(processMapping).toBeDefined();
      expect(processMapping?.signature).toContain('Mapping[str, Union[int, str]]');
      expect(processMapping?.signature).toContain('Dict[str, str]');

      const filterItems = symbols.find(s => s.name === 'filter_items');
      expect(filterItems).toBeDefined();
      expect(filterItems?.signature).toContain('Iterable[T]');
      expect(filterItems?.signature).toContain('Callable[[T], bool]');
      expect(filterItems?.signature).toContain('List[T]');

      const cache = symbols.find(s => s.name === 'Cache');
      expect(cache).toBeDefined();
      expect(cache?.signature).toContain('class Cache extends Generic[K, V]');

      const getMethod = symbols.find(s => s.name === 'get' && s.parentId === cache?.id);
      expect(getMethod).toBeDefined();
      expect(getMethod?.signature).toContain('Optional[V]');

      const fetchData = symbols.find(s => s.name === 'fetch_data');
      expect(fetchData).toBeDefined();
      expect(fetchData?.signature).toContain('async def fetch_data');
      expect(fetchData?.signature).toContain('Dict[str, Optional[str]]');

      const complexReturn = symbols.find(s => s.name === 'complex_return_type');
      expect(complexReturn).toBeDefined();
      expect(complexReturn?.signature).toContain('Union[Dict[str, List[int]], None]');
    });
  });

  describe('Relationship Extraction', () => {
    it('should extract inheritance relationships', async () => {
      const pythonCode = `
class Animal:
    pass

class Dog(Animal):
    def bark(self):
        pass

class GoldenRetriever(Dog):
    def fetch(self):
        pass
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance relationships
      const dogExtendsAnimal = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Dog' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Animal'
      );
      expect(dogExtendsAnimal).toBeDefined();

      const goldenExtendsDog = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'GoldenRetriever' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Dog'
      );
      expect(goldenExtendsDog).toBeDefined();

      console.log(`🔗 Found ${relationships.length} Python relationships`);
    });

    it('should extract method call relationships', async () => {
      const pythonCode = `
class DatabaseConnection:
    def connect(self) -> bool:
        return True

    def execute_query(self, query: str) -> list:
        return []

    def close(self) -> None:
        pass

class UserRepository:
    def __init__(self, db: DatabaseConnection):
        self.db = db

    def get_user(self, user_id: int):
        self.db.connect()
        try:
            result = self.db.execute_query(f"SELECT * FROM users WHERE id = {user_id}")
            return result[0] if result else None
        finally:
            self.db.close()

    def create_user(self, user_data: dict) -> bool:
        self.db.connect()
        query = "INSERT INTO users (name, email) VALUES (?, ?)"
        self.db.execute_query(query)
        self.db.close()
        return True

def process_user_data(repo: UserRepository, user_id: int):
    user = repo.get_user(user_id)
    if user:
        return user
    return repo.create_user({"name": "default", "email": "default@example.com"})
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      const dbConnection = symbols.find(s => s.name === 'DatabaseConnection');
      const userRepo = symbols.find(s => s.name === 'UserRepository');
      const connectMethod = symbols.find(s => s.name === 'connect');
      const executeMethod = symbols.find(s => s.name === 'execute_query');
      const getUserMethod = symbols.find(s => s.name === 'get_user');

      expect(dbConnection).toBeDefined();
      expect(userRepo).toBeDefined();
      expect(connectMethod).toBeDefined();
      expect(executeMethod).toBeDefined();
      expect(getUserMethod).toBeDefined();

      // Check that UserRepository uses DatabaseConnection
      const repoUsesDb = relationships.find(r =>
        r.kind === 'uses' &&
        r.fromSymbolId === userRepo?.id &&
        r.toSymbolId === dbConnection?.id
      );
      expect(repoUsesDb).toBeDefined();

      // Check method call relationships
      const getUserCallsConnect = relationships.find(r =>
        r.kind === 'calls' &&
        r.fromSymbolId === getUserMethod?.id &&
        r.toSymbolId === connectMethod?.id
      );
      expect(getUserCallsConnect).toBeDefined();

      const getUserCallsExecute = relationships.find(r =>
        r.kind === 'calls' &&
        r.fromSymbolId === getUserMethod?.id &&
        r.toSymbolId === executeMethod?.id
      );
      expect(getUserCallsExecute).toBeDefined();

      console.log(`📞 Found ${relationships.filter(r => r.kind === 'calls').length} call relationships`);
    });
  });

  describe('Performance and Edge Cases', () => {
    it('should handle large Python files with many symbols', async () => {
      // Generate a large Python file with many classes and methods
      const classes = Array.from({ length: 20 }, (_, i) => `
class Class${i}:
    """Test class ${i}."""
    def __init__(self, value: int = ${i}):
        self.value = value
        self._private_value = value * 2

    def get_value(self) -> int:
        return self.value

    def set_value(self, value: int) -> None:
        self.value = value

    @property
    def double_value(self) -> int:
        return self.value * 2

    @staticmethod
    def static_method_${i}() -> str:
        return "static_${i}"

    @classmethod
    def class_method_${i}(cls) -> 'Class${i}':
        return cls(${i * 10})
`).join('\n');

      const functions = Array.from({ length: 10 }, (_, i) => `
def function_${i}(param1: int, param2: str = "default_${i}") -> dict:
    """Function ${i} documentation."""
    return {"id": param1, "name": param2, "index": ${i}}
`).join('\n');

      const pythonCode = `
from typing import Dict, List, Optional, Union
import asyncio
import logging

# Constants
MAX_ITEMS = 1000
DEFAULT_CONFIG = {"debug": True, "timeout": 30}
API_ENDPOINTS = [
    "/api/v1/users",
    "/api/v1/posts",
    "/api/v1/comments"
]
${classes}
${functions}

class Manager:
    """Main manager class."""
    def __init__(self):
        self.items = []
        self.logger = logging.getLogger(__name__)

    def process_all_classes(self):
        """Process all available classes."""
        results = []
        for i in range(20):
            cls = globals()[f"Class{i}"]
            instance = cls.class_method_0()
            results.append(instance.get_value())
        return results

# Global variables
manager_instance = Manager()
class_instances = [globals()[f"Class{i}"](i) for i in range(20)]
function_results = [globals()[f"function_{i}"](i, f"test_{i}") for i in range(10)]

def main():
    """Main application entry point."""
    print("Starting application...")
    manager_instance.process_all_classes()
    print("Application finished.")

if __name__ == "__main__":
    main()
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should extract many symbols
      expect(symbols.length).toBeGreaterThan(100);

      // Check that all classes were extracted
      for (let i = 0; i < 20; i++) {
        const cls = symbols.find(s => s.name === `Class${i}`);
        expect(cls).toBeDefined();
        expect(cls?.kind).toBe(SymbolKind.Class);
      }

      // Check that all functions were extracted
      for (let i = 0; i < 10; i++) {
        const func = symbols.find(s => s.name === `function_${i}`);
        expect(func).toBeDefined();
        expect(func?.kind).toBe(SymbolKind.Function);
      }

      // Check manager class
      const manager = symbols.find(s => s.name === 'Manager');
      expect(manager).toBeDefined();
      expect(manager?.kind).toBe(SymbolKind.Class);

      // Check methods within classes
      const getValue = symbols.find(s => s.name === 'get_value');
      expect(getValue).toBeDefined();
      expect(getValue?.kind).toBe(SymbolKind.Method);

      const doubleValue = symbols.find(s => s.name === 'double_value');
      expect(doubleValue).toBeDefined();
      expect(doubleValue?.signature).toContain('@property');

      // Check constants
      const maxItems = symbols.find(s => s.name === 'MAX_ITEMS');
      expect(maxItems).toBeDefined();
      expect(maxItems?.kind).toBe(SymbolKind.Constant);

      const defaultConfig = symbols.find(s => s.name === 'DEFAULT_CONFIG');
      expect(defaultConfig).toBeDefined();
      expect(defaultConfig?.kind).toBe(SymbolKind.Constant);

      console.log(`📊 Performance test: Extracted ${symbols.length} symbols and ${relationships.length} relationships`);
    });

    it('should handle edge cases and malformed code gracefully', async () => {
      const pythonCode = `
# Edge cases and unusual Python constructs

class EmptyClass:
    pass

class OneLiner: value = 42

def empty_function(): pass

def function_with_ellipsis(): ...

def function_with_string_body(): """Just a docstring."""

# Function with complex decorators
@property
@staticmethod  # This is invalid but shouldn't crash
def weird_decorated_function():
    return "weird"

# Class with missing body (syntax error but shouldn't crash parser)
class IncompleteClass

# Multiple assignments
a = b = c = 42
x, y, z = 1, 2, 3
(first, second), third = (1, 2), 3

# Unusual lambda
weird_lambda = lambda: (lambda x: x)

# Function with *args and **kwargs
def flexible_function(*args, **kwargs):
    return args, kwargs

# Function with complex annotations
def annotated_function(
    param1: "ForwardReference",
    param2: list[dict[str, int | None]],
    /,  # Positional-only separator
    param3: str = "default",
    *args: int,
    param4: bool = True,
    **kwargs: str
) -> "ComplexReturnType":
    """Function with complex parameter annotations."""
    pass

# Class with __slots__ and descriptors
class SlottedClass:
    __slots__ = ('x', 'y')

    def __init__(self, x, y):
        self.x = x
        self.y = y

# Async class methods
class AsyncClass:
    async def async_method(self):
        await asyncio.sleep(1)

    @classmethod
    async def async_classmethod(cls):
        return cls()
`;

      const result = await parserManager.parseFile('test.py', pythonCode);
      const extractor = new PythonExtractor('python', 'test.py', pythonCode);

      // Should not throw even with malformed code
      expect(() => {
        const symbols = extractor.extractSymbols(result.tree);
        const relationships = extractor.extractRelationships(result.tree, symbols);
      }).not.toThrow();

      const symbols = extractor.extractSymbols(result.tree);

      // Should still extract valid symbols
      const emptyClass = symbols.find(s => s.name === 'EmptyClass');
      expect(emptyClass).toBeDefined();
      expect(emptyClass?.kind).toBe(SymbolKind.Class);

      const oneLiner = symbols.find(s => s.name === 'OneLiner');
      expect(oneLiner).toBeDefined();

      const emptyFunction = symbols.find(s => s.name === 'empty_function');
      expect(emptyFunction).toBeDefined();
      expect(emptyFunction?.kind).toBe(SymbolKind.Function);

      const ellipsisFunction = symbols.find(s => s.name === 'function_with_ellipsis');
      expect(ellipsisFunction).toBeDefined();

      const flexibleFunction = symbols.find(s => s.name === 'flexible_function');
      expect(flexibleFunction).toBeDefined();
      expect(flexibleFunction?.signature).toContain('*args');
      expect(flexibleFunction?.signature).toContain('**kwargs');

      const annotatedFunction = symbols.find(s => s.name === 'annotated_function');
      expect(annotatedFunction).toBeDefined();
      expect(annotatedFunction?.signature).toContain('ForwardReference');
      expect(annotatedFunction?.signature).toContain('list[dict[str, int | None]]');

      const slottedClass = symbols.find(s => s.name === 'SlottedClass');
      expect(slottedClass).toBeDefined();

      const asyncClass = symbols.find(s => s.name === 'AsyncClass');
      expect(asyncClass).toBeDefined();

      const asyncMethod = symbols.find(s => s.name === 'async_method');
      expect(asyncMethod).toBeDefined();
      expect(asyncMethod?.signature).toContain('async def async_method');

      console.log(`🛡️ Edge case test: Extracted ${symbols.length} symbols from complex code`);
    });
  });
});