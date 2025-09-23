import { describe, it, expect, beforeAll } from 'bun:test';
import { CSharpExtractor } from '../../extractors/csharp-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('CSharpExtractor', () => {
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

  describe('Namespace and Using Extraction', () => {
    it('should extract namespace declarations and using statements', async () => {
      const csharpCode = `
using System;
using System.Collections.Generic;
using static System.Math;

namespace MyCompany.MyProject
{
    // Content here
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const systemImport = symbols.find(s => s.name === 'System');
      expect(systemImport).toBeDefined();
      expect(systemImport?.kind).toBe(SymbolKind.Import);

      const staticImport = symbols.find(s => s.name === 'Math');
      expect(staticImport).toBeDefined();
      expect(staticImport?.signature).toContain('using static');

      const namespace = symbols.find(s => s.name === 'MyCompany.MyProject');
      expect(namespace).toBeDefined();
      expect(namespace?.kind).toBe(SymbolKind.Namespace);
    });
  });

  describe('Class Extraction', () => {
    it('should extract class declarations with inheritance and generics', async () => {
      const csharpCode = `
namespace MyProject
{
    public abstract class BaseEntity<T> where T : class
    {
        public int Id { get; set; }
    }

    public sealed class User : BaseEntity<User>, IEquatable<User>
    {
        private readonly string _name;

        public User(string name)
        {
            _name = name;
        }
    }

    internal class InternalClass
    {
        // Internal class content
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const baseEntity = symbols.find(s => s.name === 'BaseEntity');
      expect(baseEntity).toBeDefined();
      expect(baseEntity?.kind).toBe(SymbolKind.Class);
      expect(baseEntity?.signature).toContain('public abstract class BaseEntity<T>');
      expect(baseEntity?.visibility).toBe('public');

      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.signature).toContain('sealed');
      expect(user?.signature).toContain('BaseEntity<User>');
      expect(user?.signature).toContain('IEquatable<User>');

      const internalClass = symbols.find(s => s.name === 'InternalClass');
      expect(internalClass).toBeDefined();
      expect(internalClass?.visibility).toBe('internal');
    });
  });

  describe('Interface and Struct Extraction', () => {
    it('should extract interfaces and structs', async () => {
      const csharpCode = `
namespace MyProject
{
    public interface IRepository<T> where T : class
    {
        Task<T> GetByIdAsync(int id);
        void Delete(T entity);
    }

    public struct Point
    {
        public int X { get; }
        public int Y { get; }

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public readonly struct ReadOnlyPoint
    {
        public readonly int X;
        public readonly int Y;
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const repository = symbols.find(s => s.name === 'IRepository');
      expect(repository).toBeDefined();
      expect(repository?.kind).toBe(SymbolKind.Interface);
      expect(repository?.signature).toContain('public interface IRepository<T>');

      const point = symbols.find(s => s.name === 'Point');
      expect(point).toBeDefined();
      expect(point?.kind).toBe(SymbolKind.Struct);
      expect(point?.signature).toContain('public struct Point');

      const readOnlyPoint = symbols.find(s => s.name === 'ReadOnlyPoint');
      expect(readOnlyPoint).toBeDefined();
      expect(readOnlyPoint?.signature).toContain('readonly');
    });
  });

  describe('Method Extraction', () => {
    it('should extract methods with various modifiers and return types', async () => {
      const csharpCode = `
namespace MyProject
{
    public class Calculator
    {
        public static int Add(int a, int b)
        {
            return a + b;
        }

        public async Task<string> GetDataAsync()
        {
            return await SomeAsyncOperation();
        }

        protected virtual void ProcessData<T>(T data) where T : class
        {
            // Process data
        }

        private static readonly Func<int, int> Square = x => x * x;

        public override string ToString()
        {
            return "Calculator";
        }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const add = symbols.find(s => s.name === 'Add');
      expect(add).toBeDefined();
      expect(add?.kind).toBe(SymbolKind.Method);
      expect(add?.signature).toContain('public static int Add(int a, int b)');
      expect(add?.visibility).toBe('public');

      const getDataAsync = symbols.find(s => s.name === 'GetDataAsync');
      expect(getDataAsync).toBeDefined();
      expect(getDataAsync?.signature).toContain('async');
      expect(getDataAsync?.signature).toContain('Task<string>');

      const processData = symbols.find(s => s.name === 'ProcessData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('protected virtual');
      expect(processData?.signature).toContain('<T>');

      const toString = symbols.find(s => s.name === 'ToString');
      expect(toString).toBeDefined();
      expect(toString?.signature).toContain('override');
    });
  });

  describe('Property Extraction', () => {
    it('should extract properties with various patterns', async () => {
      const csharpCode = `
namespace MyProject
{
    public class Person
    {
        // Auto property
        public string Name { get; set; }

        // Read-only auto property
        public int Age { get; }

        // Property with private setter
        public string Email { get; private set; }

        // Full property with backing field
        private string _address;
        public string Address
        {
            get { return _address; }
            set { _address = value?.Trim(); }
        }

        // Expression-bodied property
        public string FullName => $"{FirstName} {LastName}";

        // Static property
        public static int Count { get; set; }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const name = symbols.find(s => s.name === 'Name');
      expect(name).toBeDefined();
      expect(name?.kind).toBe(SymbolKind.Property);
      expect(name?.signature).toContain('public string Name { get; set; }');

      const age = symbols.find(s => s.name === 'Age');
      expect(age).toBeDefined();
      expect(age?.signature).toContain('{ get; }');

      const email = symbols.find(s => s.name === 'Email');
      expect(email).toBeDefined();
      expect(email?.signature).toContain('private set');

      const fullName = symbols.find(s => s.name === 'FullName');
      expect(fullName).toBeDefined();
      expect(fullName?.signature).toContain('=>');

      const count = symbols.find(s => s.name === 'Count');
      expect(count).toBeDefined();
      expect(count?.signature).toContain('static');
    });
  });

  describe('Constructor Extraction', () => {
    it('should extract constructors including static constructors', async () => {
      const csharpCode = `
namespace MyProject
{
    public class Configuration
    {
        static Configuration()
        {
            // Static constructor
        }

        public Configuration()
        {
            // Default constructor
        }

        public Configuration(string path) : this()
        {
            // Constructor with base call
        }

        private Configuration(string path, bool validate) : base(path)
        {
            // Private constructor with base call
        }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const constructors = symbols.filter(s => s.kind === SymbolKind.Constructor);
      expect(constructors).toHaveLength(4);

      const staticConstructor = constructors.find(s => s.signature?.includes('static'));
      expect(staticConstructor).toBeDefined();

      const defaultConstructor = constructors.find(s => s.signature?.includes('Configuration()'));
      expect(defaultConstructor).toBeDefined();
      expect(defaultConstructor?.visibility).toBe('public');

      const privateConstructor = constructors.find(s => s.signature?.includes('private') && s.signature?.includes('bool validate'));
      expect(privateConstructor).toBeDefined();
      expect(privateConstructor?.visibility).toBe('private');
    });
  });

  describe('Field and Event Extraction', () => {
    it('should extract fields and events', async () => {
      const csharpCode = `
namespace MyProject
{
    public class EventPublisher
    {
        public event Action<string> MessageReceived;
        public static event EventHandler GlobalEvent;

        private readonly ILogger _logger;
        public const string Version = "1.0.0";
        public static readonly DateTime StartTime = DateTime.Now;

        private string _name;
        protected internal int _count;
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const messageReceived = symbols.find(s => s.name === 'MessageReceived');
      expect(messageReceived).toBeDefined();
      expect(messageReceived?.kind).toBe(SymbolKind.Event);
      expect(messageReceived?.signature).toContain('event Action<string>');

      const globalEvent = symbols.find(s => s.name === 'GlobalEvent');
      expect(globalEvent).toBeDefined();
      expect(globalEvent?.signature).toContain('static event');

      const version = symbols.find(s => s.name === 'Version');
      expect(version).toBeDefined();
      expect(version?.kind).toBe(SymbolKind.Constant);
      expect(version?.signature).toContain('const');

      const startTime = symbols.find(s => s.name === 'StartTime');
      expect(startTime).toBeDefined();
      expect(startTime?.signature).toContain('static readonly');

      const count = symbols.find(s => s.name === '_count');
      expect(count).toBeDefined();
      expect(count?.visibility).toBe('protected');
    });
  });

  describe('Enum Extraction', () => {
    it('should extract enums and enum members', async () => {
      const csharpCode = `
namespace MyProject
{
    public enum Status
    {
        Pending,
        Active,
        Inactive
    }

    [Flags]
    public enum FileAccess : byte
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4,
        All = Read | Write | Execute
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const status = symbols.find(s => s.name === 'Status');
      expect(status).toBeDefined();
      expect(status?.kind).toBe(SymbolKind.Enum);

      const pending = symbols.find(s => s.name === 'Pending');
      expect(pending).toBeDefined();
      expect(pending?.kind).toBe(SymbolKind.EnumMember);

      const fileAccess = symbols.find(s => s.name === 'FileAccess');
      expect(fileAccess).toBeDefined();
      expect(fileAccess?.signature).toContain(': byte');

      const all = symbols.find(s => s.name === 'All');
      expect(all).toBeDefined();
      expect(all?.signature).toContain('Read | Write | Execute');
    });
  });

  describe('Attribute and Record Extraction', () => {
    it('should extract attributes and handle method attributes', async () => {
      const csharpCode = `
namespace MyProject
{
    [Serializable]
    [DataContract]
    public class User
    {
        [HttpGet("/users/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<User> GetUserAsync(int id)
        {
            return null;
        }

        [JsonProperty("full_name")]
        public string FullName { get; set; }
    }

    public record Person(string FirstName, string LastName);

    public record struct Point(int X, int Y);
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.signature).toContain('[Serializable]');
      expect(user?.signature).toContain('[DataContract]');

      const getUser = symbols.find(s => s.name === 'GetUserAsync');
      expect(getUser).toBeDefined();
      expect(getUser?.signature).toContain('[HttpGet');
      expect(getUser?.signature).toContain('[Authorize');

      const fullName = symbols.find(s => s.name === 'FullName');
      expect(fullName).toBeDefined();
      expect(fullName?.signature).toContain('[JsonProperty');

      const person = symbols.find(s => s.name === 'Person');
      expect(person).toBeDefined();
      expect(person?.kind).toBe(SymbolKind.Class); // Records are classes
      expect(person?.signature).toContain('record Person');

      const point = symbols.find(s => s.name === 'Point');
      expect(point).toBeDefined();
      expect(point?.kind).toBe(SymbolKind.Struct);
      expect(point?.signature).toContain('record struct');
    });
  });

  describe('Delegate and Nested Classes', () => {
    it('should extract delegates and nested classes', async () => {
      const csharpCode = `
namespace MyProject
{
    public delegate void EventHandler<T>(T data);
    public delegate TResult Func<in T, out TResult>(T input);

    public class OuterClass
    {
        public class NestedClass
        {
            private static void NestedMethod() { }
        }

        protected internal struct NestedStruct
        {
            public int Value;
        }

        private enum NestedEnum
        {
            Option1, Option2
        }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const eventHandler = symbols.find(s => s.name === 'EventHandler');
      expect(eventHandler).toBeDefined();
      expect(eventHandler?.kind).toBe(SymbolKind.Delegate);
      expect(eventHandler?.signature).toContain('delegate void EventHandler<T>');

      const func = symbols.find(s => s.name === 'Func');
      expect(func).toBeDefined();
      expect(func?.signature).toContain('in T, out TResult');

      const nestedClass = symbols.find(s => s.name === 'NestedClass');
      expect(nestedClass).toBeDefined();
      expect(nestedClass?.kind).toBe(SymbolKind.Class);

      const nestedStruct = symbols.find(s => s.name === 'NestedStruct');
      expect(nestedStruct).toBeDefined();
      expect(nestedStruct?.kind).toBe(SymbolKind.Struct);
      expect(nestedStruct?.visibility).toBe('protected');

      const nestedEnum = symbols.find(s => s.name === 'NestedEnum');
      expect(nestedEnum).toBeDefined();
      expect(nestedEnum?.visibility).toBe('private');
    });
  });

  describe('Type Inference', () => {
    it('should infer types from C# type annotations and generics', async () => {
      const csharpCode = `
namespace MyProject
{
    public class TypeExample
    {
        public string GetName() => "test";
        public Task<List<User>> GetUsersAsync() => null;
        public void ProcessData<T>(T data) where T : class { }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      const getName = symbols.find(s => s.name === 'GetName');
      expect(getName).toBeDefined();
      expect(types.get(getName!.id)).toBe('string');

      const getUsers = symbols.find(s => s.name === 'GetUsersAsync');
      expect(getUsers).toBeDefined();
      expect(types.get(getUsers!.id)).toBe('Task<List<User>>');
    });
  });

  describe('Relationship Extraction', () => {
    it('should extract inheritance and implementation relationships', async () => {
      const csharpCode = `
namespace MyProject
{
    public interface IEntity
    {
        int Id { get; }
    }

    public abstract class BaseEntity : IEntity
    {
        public int Id { get; set; }
    }

    public class User : BaseEntity, IEquatable<User>
    {
        public string Name { get; set; }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance and implementation relationships
      expect(relationships.length).toBeGreaterThanOrEqual(1);

      const inheritance = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromId)?.name === 'User' &&
        symbols.find(s => s.id === r.toId)?.name === 'BaseEntity'
      );
      expect(inheritance).toBeDefined();
    });
  });

  describe('Modern C# Features', () => {
    it('should extract async/await patterns and nullable reference types', async () => {
      const csharpCode = `
#nullable enable
namespace ModernFeatures
{
    public class AsyncService
    {
        public async Task<string?> GetDataAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(1000, cancellationToken);
            return await ProcessDataAsync();
        }

        public async ValueTask<int> CountItemsAsync()
        {
            await foreach (var item in GetItemsAsync())
            {
                // Process item
            }
            return 42;
        }

        public async IAsyncEnumerable<string> GetItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(100, cancellationToken);
                yield return $"Item {i}";
            }
        }

        private async Task<string?> ProcessDataAsync() => await Task.FromResult("data");
    }

    public class NullableExample
    {
        public string? NullableString { get; init; }
        public required string RequiredString { get; init; }
        public string NonNullableString { get; init; } = string.Empty;
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const asyncService = symbols.find(s => s.name === 'AsyncService');
      expect(asyncService).toBeDefined();
      expect(asyncService?.kind).toBe(SymbolKind.Class);

      const getDataAsync = symbols.find(s => s.name === 'GetDataAsync');
      expect(getDataAsync).toBeDefined();
      expect(getDataAsync?.signature).toContain('async Task<string?>');
      expect(getDataAsync?.signature).toContain('CancellationToken');

      const countItemsAsync = symbols.find(s => s.name === 'CountItemsAsync');
      expect(countItemsAsync).toBeDefined();
      expect(countItemsAsync?.signature).toContain('ValueTask<int>');

      const getItemsAsync = symbols.find(s => s.name === 'GetItemsAsync');
      expect(getItemsAsync).toBeDefined();
      expect(getItemsAsync?.signature).toContain('IAsyncEnumerable<string>');
      expect(getItemsAsync?.signature).toContain('[EnumeratorCancellation]');

      const nullableExample = symbols.find(s => s.name === 'NullableExample');
      expect(nullableExample).toBeDefined();

      const nullableString = symbols.find(s => s.name === 'NullableString');
      expect(nullableString).toBeDefined();
      expect(nullableString?.signature).toContain('string?');
      expect(nullableString?.signature).toContain('init;');

      const requiredString = symbols.find(s => s.name === 'RequiredString');
      expect(requiredString).toBeDefined();
      expect(requiredString?.signature).toContain('required string');
    });

    it('should extract pattern matching and switch expressions', async () => {
      const csharpCode = `
namespace PatternMatching
{
    public abstract record Shape;
    public record Circle(double Radius) : Shape;
    public record Rectangle(double Width, double Height) : Shape;
    public record Triangle(double Base, double Height) : Shape;

    public class ShapeCalculator
    {
        public double CalculateArea(Shape shape) => shape switch
        {
            Circle { Radius: var r } => Math.PI * r * r,
            Rectangle { Width: var w, Height: var h } => w * h,
            Triangle { Base: var b, Height: var h } => 0.5 * b * h,
            _ => throw new ArgumentException("Unknown shape")
        };

        public string DescribeShape(Shape shape)
        {
            return shape switch
            {
                Circle c when c.Radius > 10 => "Large circle",
                Circle => "Small circle",
                Rectangle r when r.Width == r.Height => "Square",
                Rectangle => "Rectangle",
                Triangle => "Triangle",
                null => "No shape",
                _ => "Unknown"
            };
        }

        public bool IsLargeShape(Shape shape) => shape is Circle { Radius: > 5 } or Rectangle { Width: > 10, Height: > 10 };
    }

    public class PatternExamples
    {
        public void ProcessValue(object value)
        {
            if (value is string { Length: > 0 } str)
            {
                Console.WriteLine($"Non-empty string: {str}");
            }
            else if (value is int i and > 0)
            {
                Console.WriteLine($"Positive integer: {i}");
            }
        }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const shape = symbols.find(s => s.name === 'Shape');
      expect(shape).toBeDefined();
      expect(shape?.signature).toContain('abstract record');

      const circle = symbols.find(s => s.name === 'Circle');
      expect(circle).toBeDefined();
      expect(circle?.signature).toContain('record Circle(double Radius)');
      expect(circle?.signature).toContain(': Shape');

      const calculateArea = symbols.find(s => s.name === 'CalculateArea');
      expect(calculateArea).toBeDefined();
      expect(calculateArea?.signature).toContain('=> shape switch');

      const describeShape = symbols.find(s => s.name === 'DescribeShape');
      expect(describeShape).toBeDefined();
      expect(describeShape?.kind).toBe(SymbolKind.Method);

      const isLargeShape = symbols.find(s => s.name === 'IsLargeShape');
      expect(isLargeShape).toBeDefined();
      expect(isLargeShape?.signature).toContain('is Circle');

      const processValue = symbols.find(s => s.name === 'ProcessValue');
      expect(processValue).toBeDefined();
      expect(processValue?.signature).toContain('object value');
    });
  });

  describe('Advanced Generic and Type Features', () => {
    it('should extract complex generic constraints and covariance', async () => {
      const csharpCode = `
namespace AdvancedGenerics
{
    public interface ICovariant<out T>
    {
        T GetValue();
    }

    public interface IContravariant<in T>
    {
        void SetValue(T value);
    }

    public interface IRepository<T> where T : class, IEntity, new()
    {
        Task<T> GetByIdAsync<TKey>(TKey id) where TKey : struct, IComparable<TKey>;
    }

    public class GenericService<T, U, V> where T : class, IDisposable where U : struct where V : T, new()
    {
        public async Task<TResult> ProcessAsync<TResult, TInput>(TInput input, Func<TInput, Task<TResult>> processor)
            where TResult : class
            where TInput : notnull
        {
            return await processor(input);
        }

        public void HandleNullableTypes<TNullable>(TNullable? nullable) where TNullable : struct
        {
            if (nullable.HasValue)
            {
                Console.WriteLine(nullable.Value);
            }
        }
    }

    public readonly struct ValueTuple<T1, T2, T3>
    {
        public readonly T1 Item1;
        public readonly T2 Item2;
        public readonly T3 Item3;

        public ValueTuple(T1 item1, T2 item2, T3 item3)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
        }

        public void Deconstruct(out T1 item1, out T2 item2, out T3 item3)
        {
            item1 = Item1;
            item2 = Item2;
            item3 = Item3;
        }
    }

    public class TupleExamples
    {
        public (string Name, int Age, DateTime Birth) GetPersonInfo() => ("John", 30, DateTime.Now);
        public (int Sum, int Product) Calculate(int a, int b) => (a + b, a * b);
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const covariant = symbols.find(s => s.name === 'ICovariant');
      expect(covariant).toBeDefined();
      expect(covariant?.signature).toContain('out T');

      const contravariant = symbols.find(s => s.name === 'IContravariant');
      expect(contravariant).toBeDefined();
      expect(contravariant?.signature).toContain('in T');

      const repository = symbols.find(s => s.name === 'IRepository');
      expect(repository).toBeDefined();
      expect(repository?.signature).toContain('where T : class, IEntity, new()');

      const getByIdAsync = symbols.find(s => s.name === 'GetByIdAsync');
      expect(getByIdAsync).toBeDefined();
      expect(getByIdAsync?.signature).toContain('<TKey>');
      expect(getByIdAsync?.signature).toContain('where TKey : struct');

      const genericService = symbols.find(s => s.name === 'GenericService');
      expect(genericService).toBeDefined();
      expect(genericService?.signature).toContain('<T, U, V>');
      expect(genericService?.signature).toContain('where T : class, IDisposable');

      const processAsync = symbols.find(s => s.name === 'ProcessAsync');
      expect(processAsync).toBeDefined();
      expect(processAsync?.signature).toContain('<TResult, TInput>');
      expect(processAsync?.signature).toContain('where TResult : class');
      expect(processAsync?.signature).toContain('where TInput : notnull');

      const handleNullableTypes = symbols.find(s => s.name === 'HandleNullableTypes');
      expect(handleNullableTypes).toBeDefined();
      expect(handleNullableTypes?.signature).toContain('TNullable?');

      const valueTuple = symbols.find(s => s.name === 'ValueTuple');
      expect(valueTuple).toBeDefined();
      expect(valueTuple?.kind).toBe(SymbolKind.Struct);
      expect(valueTuple?.signature).toContain('readonly struct');

      const deconstruct = symbols.find(s => s.name === 'Deconstruct');
      expect(deconstruct).toBeDefined();
      expect(deconstruct?.signature).toContain('out T1');

      const getPersonInfo = symbols.find(s => s.name === 'GetPersonInfo');
      expect(getPersonInfo).toBeDefined();
      expect(getPersonInfo?.signature).toContain('(string Name, int Age, DateTime Birth)');
    });
  });

  describe('LINQ and Lambda Expressions', () => {
    it('should extract LINQ queries and lambda expressions', async () => {
      const csharpCode = `
using System.Linq.Expressions;

namespace LinqExamples
{
    public class QueryService
    {
        public IQueryable<TResult> QueryData<T, TResult>(IQueryable<T> source, Expression<Func<T, bool>> predicate, Expression<Func<T, TResult>> selector)
        {
            return source.Where(predicate).Select(selector);
        }

        public async Task<List<User>> GetFilteredUsersAsync(List<User> users)
        {
            var result = from user in users
                        where user.Age > 18 && user.IsActive
                        let fullName = $"{user.FirstName} {user.LastName}"
                        orderby user.LastName, user.FirstName
                        select new User
                        {
                            Id = user.Id,
                            FullName = fullName,
                            Email = user.Email?.ToLower()
                        };

            return await Task.FromResult(result.ToList());
        }

        public void ProcessItems<T>(IEnumerable<T> items, Action<T> processor)
        {
            items.AsParallel()
                 .Where(item => item != null)
                 .ForAll(processor);
        }

        public Func<int, int> CreateMultiplier(int factor) => x => x * factor;

        public Expression<Func<T, bool>> CreatePredicate<T>(string propertyName, object value)
        {
            var parameter = Expression.Parameter(typeof(T), "x");
            var property = Expression.Property(parameter, propertyName);
            var constant = Expression.Constant(value);
            var equality = Expression.Equal(property, constant);
            return Expression.Lambda<Func<T, bool>>(equality, parameter);
        }
    }

    public class LocalFunctionExamples
    {
        public int CalculateFactorial(int n)
        {
            return n <= 1 ? 1 : CalculateFactorialLocal(n);

            static int CalculateFactorialLocal(int num)
            {
                if (num <= 1) return 1;
                return num * CalculateFactorialLocal(num - 1);
            }
        }

        public async Task<string> ProcessDataAsync(string input)
        {
            return await ProcessLocalAsync();

            async Task<string> ProcessLocalAsync()
            {
                await Task.Delay(100);
                return input.ToUpper();
            }
        }
    }

    public class User
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public int Age { get; set; }
        public bool IsActive { get; set; }
        public string FullName { get; set; } = string.Empty;
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const queryService = symbols.find(s => s.name === 'QueryService');
      expect(queryService).toBeDefined();
      expect(queryService?.kind).toBe(SymbolKind.Class);

      const queryData = symbols.find(s => s.name === 'QueryData');
      expect(queryData).toBeDefined();
      expect(queryData?.signature).toContain('Expression<Func<T, bool>>');
      expect(queryData?.signature).toContain('Expression<Func<T, TResult>>');

      const getFilteredUsersAsync = symbols.find(s => s.name === 'GetFilteredUsersAsync');
      expect(getFilteredUsersAsync).toBeDefined();
      expect(getFilteredUsersAsync?.signature).toContain('async Task<List<User>>');

      const processItems = symbols.find(s => s.name === 'ProcessItems');
      expect(processItems).toBeDefined();
      expect(processItems?.signature).toContain('Action<T>');

      const createMultiplier = symbols.find(s => s.name === 'CreateMultiplier');
      expect(createMultiplier).toBeDefined();
      expect(createMultiplier?.signature).toContain('Func<int, int>');
      expect(createMultiplier?.signature).toContain('=> x => x * factor');

      const createPredicate = symbols.find(s => s.name === 'CreatePredicate');
      expect(createPredicate).toBeDefined();
      expect(createPredicate?.signature).toContain('Expression<Func<T, bool>>');

      const localFunctionExamples = symbols.find(s => s.name === 'LocalFunctionExamples');
      expect(localFunctionExamples).toBeDefined();

      const calculateFactorial = symbols.find(s => s.name === 'CalculateFactorial');
      expect(calculateFactorial).toBeDefined();
      expect(calculateFactorial?.kind).toBe(SymbolKind.Method);

      const processDataAsync = symbols.find(s => s.name === 'ProcessDataAsync');
      expect(processDataAsync).toBeDefined();
      expect(processDataAsync?.signature).toContain('async Task<string>');

      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.kind).toBe(SymbolKind.Class);
    });
  });

  describe('Exception Handling and Resource Management', () => {
    it('should extract exception handling and using statements', async () => {
      const csharpCode = `
namespace ExceptionHandling
{
    public class CustomException : Exception
    {
        public string ErrorCode { get; }

        public CustomException(string message, string errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public CustomException(string message, string errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    public class ResourceManager : IDisposable, IAsyncDisposable
    {
        private bool _disposed = false;
        private readonly FileStream? _fileStream;

        public ResourceManager(string filePath)
        {
            try
            {
                _fileStream = new FileStream(filePath, FileMode.Open);
            }
            catch (FileNotFoundException ex)
            {
                throw new CustomException($"File not found: {filePath}", "FILE_NOT_FOUND", ex);
            }
            catch (UnauthorizedAccessException)
            {
                throw new CustomException("Access denied", "ACCESS_DENIED");
            }
        }

        public async Task<string> ReadDataAsync()
        {
            try
            {
                using var reader = new StreamReader(_fileStream!);
                return await reader.ReadToEndAsync();
            }
            catch (IOException ex) when (ex.Message.Contains("network"))
            {
                // Handle network-related IO errors
                throw new CustomException("Network error occurred", "NETWORK_ERROR", ex);
            }
            catch (IOException)
            {
                // Handle other IO errors
                throw;
            }
            finally
            {
                Console.WriteLine("Read operation completed");
            }
        }

        public void ProcessData()
        {
            using (var connection = new SqlConnection("connection_string"))
            using (var command = new SqlCommand("SELECT * FROM Users", connection))
            {
                try
                {
                    connection.Open();
                    var result = command.ExecuteScalar();
                }
                catch (SqlException ex) when (ex.Number == 2)
                {
                    throw new CustomException("Database timeout", "DB_TIMEOUT", ex);
                }
                catch (SqlException ex)
                {
                    throw new CustomException($"Database error: {ex.Message}", "DB_ERROR", ex);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _fileStream?.Dispose();
                }
                _disposed = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
            Dispose(false);
            GC.SuppressFinalize(this);
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_fileStream is not null)
            {
                await _fileStream.DisposeAsync();
            }
        }

        ~ResourceManager()
        {
            Dispose(false);
        }
    }

    public static class ExceptionUtilities
    {
        public static void HandleException(Exception ex)
        {
            switch (ex)
            {
                case CustomException customEx:
                    Console.WriteLine($"Custom error: {customEx.ErrorCode}");
                    break;
                case ArgumentNullException argEx:
                    Console.WriteLine($"Null argument: {argEx.ParamName}");
                    break;
                case Exception generalEx:
                    Console.WriteLine($"General error: {generalEx.Message}");
                    break;
            }
        }
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      const customException = symbols.find(s => s.name === 'CustomException');
      expect(customException).toBeDefined();
      expect(customException?.kind).toBe(SymbolKind.Class);
      expect(customException?.signature).toContain(': Exception');

      const errorCode = symbols.find(s => s.name === 'ErrorCode');
      expect(errorCode).toBeDefined();
      expect(errorCode?.kind).toBe(SymbolKind.Property);

      const resourceManager = symbols.find(s => s.name === 'ResourceManager');
      expect(resourceManager).toBeDefined();
      expect(resourceManager?.signature).toContain(': IDisposable, IAsyncDisposable');

      const constructors = symbols.filter(s => s.kind === SymbolKind.Constructor && s.signature?.includes('ResourceManager'));
      expect(constructors.length).toBeGreaterThanOrEqual(1);

      const readDataAsync = symbols.find(s => s.name === 'ReadDataAsync');
      expect(readDataAsync).toBeDefined();
      expect(readDataAsync?.signature).toContain('async Task<string>');

      const processData = symbols.find(s => s.name === 'ProcessData');
      expect(processData).toBeDefined();
      expect(processData?.kind).toBe(SymbolKind.Method);

      const dispose = symbols.find(s => s.name === 'Dispose' && !s.signature?.includes('bool'));
      expect(dispose).toBeDefined();
      expect(dispose?.visibility).toBe('public');

      const disposeProtected = symbols.find(s => s.name === 'Dispose' && s.signature?.includes('bool'));
      expect(disposeProtected).toBeDefined();
      expect(disposeProtected?.visibility).toBe('protected');

      const disposeAsync = symbols.find(s => s.name === 'DisposeAsync');
      expect(disposeAsync).toBeDefined();
      expect(disposeAsync?.signature).toContain('ValueTask');

      const disposeAsyncCore = symbols.find(s => s.name === 'DisposeAsyncCore');
      expect(disposeAsyncCore).toBeDefined();
      expect(disposeAsyncCore?.visibility).toBe('protected');

      const finalizer = symbols.find(s => s.signature?.includes('~ResourceManager'));
      expect(finalizer).toBeDefined();

      const exceptionUtilities = symbols.find(s => s.name === 'ExceptionUtilities');
      expect(exceptionUtilities).toBeDefined();
      expect(exceptionUtilities?.signature).toContain('static class');

      const handleException = symbols.find(s => s.name === 'HandleException');
      expect(handleException).toBeDefined();
      expect(handleException?.signature).toContain('static void');
    });
  });

  describe('C# Testing Patterns', () => {
    it('should extract xUnit, NUnit, and MSTest patterns', async () => {
      const csharpCode = `
using Xunit;
using NUnit.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;

namespace TestExamples
{
    // xUnit Test Class
    public class XUnitTests : IClassFixture<DatabaseFixture>, IDisposable
    {
        private readonly DatabaseFixture _fixture;
        private readonly Mock<IUserService> _mockUserService;

        public XUnitTests(DatabaseFixture fixture)
        {
            _fixture = fixture;
            _mockUserService = new Mock<IUserService>();
        }

        [Fact]
        public void ShouldCalculateCorrectly()
        {
            // Arrange
            var calculator = new Calculator();

            // Act
            var result = calculator.Add(2, 3);

            // Assert
            Assert.Equal(5, result);
            result.Should().Be(5);
        }

        [Theory]
        [InlineData(2, 3, 5)]
        [InlineData(-1, 1, 0)]
        [InlineData(0, 0, 0)]
        public void ShouldAddNumbers(int a, int b, int expected)
        {
            var calculator = new Calculator();
            var result = calculator.Add(a, b);
            Assert.Equal(expected, result);
        }

        [Fact]
        public async Task ShouldHandleAsync()
        {
            _mockUserService.Setup(x => x.GetUserAsync(It.IsAny<int>()))
                           .ReturnsAsync(new User { Id = 1, Name = "Test" });

            var result = await _mockUserService.Object.GetUserAsync(1);

            Assert.NotNull(result);
            result.Name.Should().Be("Test");
        }

        public void Dispose()
        {
            _mockUserService?.Reset();
        }
    }

    // NUnit Test Class
    [TestFixture]
    [Category("Integration")]
    public class NUnitTests
    {
        private Calculator _calculator = null!;
        private Mock<ILogger> _mockLogger = null!;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            // One-time setup
        }

        [SetUp]
        public void Setup()
        {
            _calculator = new Calculator();
            _mockLogger = new Mock<ILogger>();
        }

        [Test]
        [TestCase(1, 2, 3)]
        [TestCase(10, 20, 30)]
        [TestCase(-5, 5, 0)]
        public void Add_ShouldReturnCorrectSum(int a, int b, int expected)
        {
            var result = _calculator.Add(a, b);
            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        [Timeout(5000)]
        public async Task ProcessData_ShouldCompleteWithinTimeout()
        {
            await Task.Delay(1000);
            Assert.Pass();
        }

        [Test]
        [Ignore("Temporarily disabled")]
        public void IgnoredTest()
        {
            Assert.Fail("This should not run");
        }

        [TearDown]
        public void TearDown()
        {
            _mockLogger?.Reset();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // One-time cleanup
        }
    }

    // MSTest Test Class
    [TestClass]
    [TestCategory("Unit")]
    public class MSTestTests
    {
        private TestContext? _testContext;
        private Calculator? _calculator;

        public TestContext TestContext
        {
            get => _testContext!;
            set => _testContext = value;
        }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            // Class-level initialization
        }

        [TestInitialize]
        public void TestInitialize()
        {
            _calculator = new Calculator();
        }

        [TestMethod]
        [DataRow(1, 2, 3)]
        [DataRow(10, 15, 25)]
        [DataRow(-3, 3, 0)]
        public void Add_WithDataRows_ShouldReturnExpectedResult(int a, int b, int expected)
        {
            var result = _calculator!.Add(a, b);
            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Divide_ByZero_ShouldThrowException()
        {
            _calculator!.Divide(10, 0);
        }

        [TestMethod]
        [Owner("TeamA")]
        [Priority(1)]
        public void HighPriorityTest()
        {
            Assert.IsTrue(true);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _calculator = null;
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Class-level cleanup
        }
    }

    public class DatabaseFixture : IDisposable
    {
        public string ConnectionString { get; } = "test_connection";
        public void Dispose() { }
    }

    public interface IUserService
    {
        Task<User> GetUserAsync(int id);
    }

    public interface ILogger
    {
        void Log(string message);
    }

    public class Calculator
    {
        public int Add(int a, int b) => a + b;
        public int Divide(int a, int b) => a / b;
    }

    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
`;

      const result = await parserManager.parseFile('test.cs', csharpCode);
      const extractor = new CSharpExtractor('c_sharp', 'test.cs', csharpCode);
      const symbols = extractor.extractSymbols(result.tree);

      // xUnit Tests
      const xunitTests = symbols.find(s => s.name === 'XUnitTests');
      expect(xunitTests).toBeDefined();
      expect(xunitTests?.signature).toContain(': IClassFixture<DatabaseFixture>, IDisposable');

      const shouldCalculateCorrectly = symbols.find(s => s.name === 'ShouldCalculateCorrectly');
      expect(shouldCalculateCorrectly).toBeDefined();
      expect(shouldCalculateCorrectly?.signature).toContain('[Fact]');

      const shouldAddNumbers = symbols.find(s => s.name === 'ShouldAddNumbers');
      expect(shouldAddNumbers).toBeDefined();
      expect(shouldAddNumbers?.signature).toContain('[Theory]');
      expect(shouldAddNumbers?.signature).toContain('[InlineData');

      const shouldHandleAsync = symbols.find(s => s.name === 'ShouldHandleAsync');
      expect(shouldHandleAsync).toBeDefined();
      expect(shouldHandleAsync?.signature).toContain('async Task');

      // NUnit Tests
      const nunitTests = symbols.find(s => s.name === 'NUnitTests');
      expect(nunitTests).toBeDefined();
      expect(nunitTests?.signature).toContain('[TestFixture]');
      expect(nunitTests?.signature).toContain('[Category("Integration")]');

      const oneTimeSetup = symbols.find(s => s.name === 'OneTimeSetup');
      expect(oneTimeSetup).toBeDefined();
      expect(oneTimeSetup?.signature).toContain('[OneTimeSetUp]');

      const setup = symbols.find(s => s.name === 'Setup');
      expect(setup).toBeDefined();
      expect(setup?.signature).toContain('[SetUp]');

      const addShouldReturnCorrectSum = symbols.find(s => s.name === 'Add_ShouldReturnCorrectSum');
      expect(addShouldReturnCorrectSum).toBeDefined();
      expect(addShouldReturnCorrectSum?.signature).toContain('[TestCase');

      const processDataShouldCompleteWithinTimeout = symbols.find(s => s.name === 'ProcessData_ShouldCompleteWithinTimeout');
      expect(processDataShouldCompleteWithinTimeout).toBeDefined();
      expect(processDataShouldCompleteWithinTimeout?.signature).toContain('[Timeout(5000)]');

      const ignoredTest = symbols.find(s => s.name === 'IgnoredTest');
      expect(ignoredTest).toBeDefined();
      expect(ignoredTest?.signature).toContain('[Ignore');

      // MSTest Tests
      const msTestTests = symbols.find(s => s.name === 'MSTestTests');
      expect(msTestTests).toBeDefined();
      expect(msTestTests?.signature).toContain('[TestClass]');
      expect(msTestTests?.signature).toContain('[TestCategory("Unit")]');

      const classInitialize = symbols.find(s => s.name === 'ClassInitialize');
      expect(classInitialize).toBeDefined();
      expect(classInitialize?.signature).toContain('[ClassInitialize]');
      expect(classInitialize?.signature).toContain('static');

      const testInitialize = symbols.find(s => s.name === 'TestInitialize');
      expect(testInitialize).toBeDefined();
      expect(testInitialize?.signature).toContain('[TestInitialize]');

      const addWithDataRows = symbols.find(s => s.name === 'Add_WithDataRows_ShouldReturnExpectedResult');
      expect(addWithDataRows).toBeDefined();
      expect(addWithDataRows?.signature).toContain('[DataRow');

      const divideByZero = symbols.find(s => s.name === 'Divide_ByZero_ShouldThrowException');
      expect(divideByZero).toBeDefined();
      expect(divideByZero?.signature).toContain('[ExpectedException');

      const highPriorityTest = symbols.find(s => s.name === 'HighPriorityTest');
      expect(highPriorityTest).toBeDefined();
      expect(highPriorityTest?.signature).toContain('[Owner("TeamA")]');
      expect(highPriorityTest?.signature).toContain('[Priority(1)]');

      // Supporting classes
      const databaseFixture = symbols.find(s => s.name === 'DatabaseFixture');
      expect(databaseFixture).toBeDefined();
      expect(databaseFixture?.signature).toContain(': IDisposable');

      const userService = symbols.find(s => s.name === 'IUserService');
      expect(userService).toBeDefined();
      expect(userService?.kind).toBe(SymbolKind.Interface);

      const calculator = symbols.find(s => s.name === 'Calculator');
      expect(calculator).toBeDefined();
      expect(calculator?.kind).toBe(SymbolKind.Class);
    });
  });

  describe('Performance Testing', () => {
    it('should handle large codebases with many symbols efficiently', async () => {
      const generateClass = (index: number) => `
    public class Service${index} : IService<Entity${index}>
    {
        private readonly IRepository<Entity${index}> _repository;
        private readonly ILogger<Service${index}> _logger;
        private readonly IMapper _mapper;

        public Service${index}(IRepository<Entity${index}> repository, ILogger<Service${index}> logger, IMapper mapper)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        public async Task<Entity${index}> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Retrieving entity with ID {Id}", id);
                var entity = await _repository.GetByIdAsync(id, cancellationToken);
                return entity ?? throw new NotFoundException($"Entity${index} with ID {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving entity with ID {Id}", id);
                throw;
            }
        }

        public async Task<IEnumerable<Entity${index}>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await _repository.GetAllAsync(cancellationToken);
        }

        public async Task<Entity${index}> CreateAsync(CreateEntity${index}Request request, CancellationToken cancellationToken = default)
        {
            var entity = _mapper.Map<Entity${index}>(request);
            return await _repository.CreateAsync(entity, cancellationToken);
        }

        public async Task<Entity${index}> UpdateAsync(int id, UpdateEntity${index}Request request, CancellationToken cancellationToken = default)
        {
            var existingEntity = await GetByIdAsync(id, cancellationToken);
            _mapper.Map(request, existingEntity);
            return await _repository.UpdateAsync(existingEntity, cancellationToken);
        }

        public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            var entity = await GetByIdAsync(id, cancellationToken);
            await _repository.DeleteAsync(entity, cancellationToken);
        }
    }

    public class Entity${index} : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public bool IsActive { get; set; } = true;
        public decimal Value${index} { get; set; }
        public EntityType${index} Type { get; set; }

        public virtual ICollection<RelatedEntity${index}> RelatedEntities { get; set; } = new List<RelatedEntity${index}>();
    }

    public enum EntityType${index}
    {
        Type1,
        Type2,
        Type3
    }

    public record CreateEntity${index}Request(string Name, string Description, decimal Value${index}, EntityType${index} Type);
    public record UpdateEntity${index}Request(string Name, string Description, decimal Value${index}, bool IsActive);

    public class RelatedEntity${index}
    {
        public int Id { get; set; }
        public int Entity${index}Id { get; set; }
        public virtual Entity${index} Entity${index} { get; set; } = null!;
        public string RelatedData { get; set; } = string.Empty;
    }`;

      const largeCodebase = `
namespace PerformanceTest
{
    public interface IService<T> where T : BaseEntity
    {
        Task<T> GetByIdAsync(int id, CancellationToken cancellationToken = default);
        Task<IEnumerable<T>> GetAllAsync(CancellationToken cancellationToken = default);
    }

    public interface IRepository<T> where T : BaseEntity
    {
        Task<T> GetByIdAsync(int id, CancellationToken cancellationToken = default);
        Task<IEnumerable<T>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<T> CreateAsync(T entity, CancellationToken cancellationToken = default);
        Task<T> UpdateAsync(T entity, CancellationToken cancellationToken = default);
        Task DeleteAsync(T entity, CancellationToken cancellationToken = default);
    }

    public abstract class BaseEntity
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public interface IMapper
    {
        TDestination Map<TDestination>(object source);
        void Map<TSource, TDestination>(TSource source, TDestination destination);
    }

    public class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message) { }
    }
${Array.from({ length: 20 }, (_, i) => generateClass(i + 1)).join('')}
}
`;

      const startTime = performance.now();
      const result = await parserManager.parseFile('large-test.cs', largeCodebase);
      const extractor = new CSharpExtractor('c_sharp', 'large-test.cs', largeCodebase);
      const symbols = extractor.extractSymbols(result.tree);
      const endTime = performance.now();

      // Should extract all symbols efficiently
      expect(symbols.length).toBeGreaterThan(200); // Many symbols expected
      expect(endTime - startTime).toBeLessThan(5000); // Should complete within 5 seconds

      // Verify some key symbols are extracted
      const services = symbols.filter(s => s.name.startsWith('Service') && s.kind === SymbolKind.Class);
      expect(services.length).toBe(20);

      const entities = symbols.filter(s => s.name.startsWith('Entity') && s.kind === SymbolKind.Class && !s.name.includes('Related'));
      expect(entities.length).toBe(20);

      const getByIdMethods = symbols.filter(s => s.name === 'GetByIdAsync');
      expect(getByIdMethods.length).toBeGreaterThanOrEqual(20);

      const enums = symbols.filter(s => s.name.startsWith('EntityType') && s.kind === SymbolKind.Enum);
      expect(enums.length).toBe(20);

      const records = symbols.filter(s => s.name.includes('Request') && s.signature?.includes('record'));
      expect(records.length).toBe(40); // 2 records per entity (Create and Update)
    });
  });

  describe('Edge Cases and Error Handling', () => {
    it('should handle malformed code and complex constructs', async () => {
      const complexCode = `
#if DEBUG
#define TRACE_ENABLED
#endif

namespace EdgeCases
{
    // Nested generic constraints
    public class ComplexGeneric<T, U, V>
        where T : class, IComparable<T>, new()
        where U : struct, IEquatable<U>
        where V : T, IDisposable
    {
        public async Task<TResult> ProcessAsync<TResult, TInput>(
            TInput input,
            Func<TInput, Task<TResult>> processor,
            CancellationToken cancellationToken = default)
        where TResult : class
        where TInput : notnull
        {
            return await processor(input);
        }
    }

    // Complex inheritance chain
    public abstract class BaseClass<T> : IDisposable, IAsyncDisposable where T : class
    {
        protected abstract Task<T> ProcessInternalAsync();
        public abstract void Dispose();
        public abstract ValueTask DisposeAsync();
    }

    public class DerivedClass<T, U> : BaseClass<T> where T : class where U : struct
    {
        protected override async Task<T> ProcessInternalAsync() => await Task.FromResult(default(T)!);
        public override void Dispose() { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Operator overloading
    public struct ComplexNumber
    {
        public double Real { get; }
        public double Imaginary { get; }

        public ComplexNumber(double real, double imaginary)
        {
            Real = real;
            Imaginary = imaginary;
        }

        public static ComplexNumber operator +(ComplexNumber a, ComplexNumber b)
            => new(a.Real + b.Real, a.Imaginary + b.Imaginary);

        public static ComplexNumber operator -(ComplexNumber a, ComplexNumber b)
            => new(a.Real - b.Real, a.Imaginary - b.Imaginary);

        public static implicit operator ComplexNumber(double real) => new(real, 0);
        public static explicit operator double(ComplexNumber complex) => complex.Real;

        public static bool operator ==(ComplexNumber left, ComplexNumber right) => left.Equals(right);
        public static bool operator !=(ComplexNumber left, ComplexNumber right) => !left.Equals(right);

        public override bool Equals(object? obj) => obj is ComplexNumber other && Equals(other);
        public bool Equals(ComplexNumber other) => Real == other.Real && Imaginary == other.Imaginary;
        public override int GetHashCode() => HashCode.Combine(Real, Imaginary);
    }

    // Indexers and properties
    public class IndexedCollection<T>
    {
        private readonly List<T> _items = new();

        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public T this[string key]
        {
            get => _items.FirstOrDefault()!;
            set { /* Implementation */ }
        }

        public int Count => _items.Count;
        public bool IsEmpty => _items.Count == 0;
    }

    // Global using and file-scoped namespace (modern C#)
    // Note: These would normally be in separate files

    // Unsafe code
    public unsafe class UnsafeOperations
    {
        public static unsafe int ProcessPointer(int* ptr)
        {
            return *ptr * 2;
        }

        public static unsafe void ProcessArray(int[] array)
        {
            fixed (int* ptr = array)
            {
                for (int i = 0; i < array.Length; i++)
                {
                    *(ptr + i) *= 2;
                }
            }
        }
    }

    // Malformed code that should be handled gracefully
    /* This is intentionally malformed to test error handling */
    /*
    public class IncompleteClass
    {
        public void IncompleteMethod(
        // Missing closing parenthesis and brace
    */

#if TRACE_ENABLED
    public static class TraceUtilities
    {
        [Conditional("TRACE")]
        public static void TraceMessage(string message)
        {
            Console.WriteLine($"TRACE: {message}");
        }
    }
#endif

    // Raw string literals and other modern features
    public class ModernStringFeatures
    {
        public const string JsonTemplate = """
        {
            "name": "{{name}}",
            "value": {{value}}
        }
        """;

        public static string FormatJson(string name, int value) =>
            JsonTemplate.Replace("{{name}}", name).Replace("{{value}}", value.ToString());
    }
}
`;

      const result = await parserManager.parseFile('complex-test.cs', complexCode);
      const extractor = new CSharpExtractor('c_sharp', 'complex-test.cs', complexCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should handle complex generics
      const complexGeneric = symbols.find(s => s.name === 'ComplexGeneric');
      expect(complexGeneric).toBeDefined();
      expect(complexGeneric?.signature).toContain('<T, U, V>');
      expect(complexGeneric?.signature).toContain('where T : class');

      const processAsync = symbols.find(s => s.name === 'ProcessAsync');
      expect(processAsync).toBeDefined();
      expect(processAsync?.signature).toContain('<TResult, TInput>');

      // Should handle inheritance
      const baseClass = symbols.find(s => s.name === 'BaseClass');
      expect(baseClass).toBeDefined();
      expect(baseClass?.signature).toContain('abstract class');

      const derivedClass = symbols.find(s => s.name === 'DerivedClass');
      expect(derivedClass).toBeDefined();
      expect(derivedClass?.signature).toContain(': BaseClass<T>');

      // Should handle operators
      const complexNumber = symbols.find(s => s.name === 'ComplexNumber');
      expect(complexNumber).toBeDefined();
      expect(complexNumber?.kind).toBe(SymbolKind.Struct);

      const operators = symbols.filter(s => s.signature?.includes('operator'));
      expect(operators.length).toBeGreaterThanOrEqual(4); // +, -, ==, !=, implicit, explicit

      // Should handle indexers
      const indexedCollection = symbols.find(s => s.name === 'IndexedCollection');
      expect(indexedCollection).toBeDefined();

      const indexers = symbols.filter(s => s.signature?.includes('this['));
      expect(indexers.length).toBeGreaterThanOrEqual(2); // int and string indexers

      // Should handle unsafe code
      const unsafeOperations = symbols.find(s => s.name === 'UnsafeOperations');
      expect(unsafeOperations).toBeDefined();
      expect(unsafeOperations?.signature).toContain('unsafe class');

      const processPointer = symbols.find(s => s.name === 'ProcessPointer');
      expect(processPointer).toBeDefined();
      expect(processPointer?.signature).toContain('unsafe');

      // Should handle preprocessor directives
      const traceUtilities = symbols.find(s => s.name === 'TraceUtilities');
      // May or may not be present depending on preprocessor conditions

      // Should handle modern string features
      const modernStringFeatures = symbols.find(s => s.name === 'ModernStringFeatures');
      expect(modernStringFeatures).toBeDefined();

      const jsonTemplate = symbols.find(s => s.name === 'JsonTemplate');
      expect(jsonTemplate).toBeDefined();
      expect(jsonTemplate?.kind).toBe(SymbolKind.Constant);

      // Should not crash on malformed code
      expect(symbols.length).toBeGreaterThan(20); // Should extract valid symbols despite malformed sections
    });
  });
});