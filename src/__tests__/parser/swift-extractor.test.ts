import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { SwiftExtractor } from '../../extractors/swift-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('SwiftExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Class and Struct Extraction', () => {
    it('should extract classes, structs, and their members', async () => {
      const swiftCode = `
class Vehicle {
    var speed: Int = 0
    private let maxSpeed: Int

    init(maxSpeed: Int) {
        self.maxSpeed = maxSpeed
    }

    func accelerate() {
        speed += 1
    }

    deinit {
        print("Vehicle deallocated")
    }
}

struct Point {
    let x: Double
    let y: Double

    mutating func move(dx: Double, dy: Double) {
        x += dx
        y += dy
    }
}

public class Car: Vehicle {
    override func accelerate() {
        speed += 2
    }
}
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Class extraction
      const vehicle = symbols.find(s => s.name === 'Vehicle');
      expect(vehicle).toBeDefined();
      expect(vehicle?.kind).toBe(SymbolKind.Class);
      expect(vehicle?.signature).toContain('class Vehicle');

      // Properties
      const speed = symbols.find(s => s.name === 'speed');
      expect(speed).toBeDefined();
      expect(speed?.kind).toBe(SymbolKind.Property);
      expect(speed?.signature).toContain('var speed: Int');

      const maxSpeed = symbols.find(s => s.name === 'maxSpeed');
      expect(maxSpeed).toBeDefined();
      expect(maxSpeed?.visibility).toBe('private');
      expect(maxSpeed?.signature).toContain('let maxSpeed: Int');

      // Methods
      const accelerate = symbols.find(s => s.name === 'accelerate');
      expect(accelerate).toBeDefined();
      expect(accelerate?.kind).toBe(SymbolKind.Method);

      // Initializer
      const initializer = symbols.find(s => s.name === 'init');
      expect(initializer).toBeDefined();
      expect(initializer?.kind).toBe(SymbolKind.Constructor);
      expect(initializer?.signature).toContain('init(maxSpeed: Int)');

      // Deinitializer
      const deinitializer = symbols.find(s => s.name === 'deinit');
      expect(deinitializer).toBeDefined();
      expect(deinitializer?.kind).toBe(SymbolKind.Destructor);

      // Struct extraction
      const point = symbols.find(s => s.name === 'Point');
      expect(point).toBeDefined();
      expect(point?.kind).toBe(SymbolKind.Struct);

      // Mutating method
      const move = symbols.find(s => s.name === 'move');
      expect(move).toBeDefined();
      expect(move?.signature).toContain('mutating func move');

      // Inheritance
      const car = symbols.find(s => s.name === 'Car');
      expect(car).toBeDefined();
      expect(car?.visibility).toBe('public');
      expect(car?.signature).toContain('Car: Vehicle');

      // Override method
      const carAccelerate = symbols.find(s => s.name === 'accelerate' && s.parentId === car?.id);
      expect(carAccelerate).toBeDefined();
      expect(carAccelerate?.signature).toContain('override');
    });
  });

  describe('Protocol and Extension Extraction', () => {
    it('should extract protocols, extensions, and conformances', async () => {
      const swiftCode = `
protocol Drawable {
    func draw()
    var area: Double { get }
    static var defaultColor: String { get set }
}

protocol Named {
    var name: String { get }
}

class Circle: Drawable, Named {
    let radius: Double
    let name: String

    init(radius: Double, name: String) {
        self.radius = radius
        self.name = name
    }

    func draw() {
        print("Drawing circle")
    }

    var area: Double {
        return Double.pi * radius * radius
    }

    static var defaultColor: String = "blue"
}

extension Circle {
    convenience init(diameter: Double) {
        self.init(radius: diameter / 2.0, name: "Unnamed")
    }

    func circumference() -> Double {
        return 2.0 * Double.pi * radius
    }
}

extension String {
    func reversed() -> String {
        return String(self.reversed())
    }
}
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Protocol extraction
      const drawable = symbols.find(s => s.name === 'Drawable');
      expect(drawable).toBeDefined();
      expect(drawable?.kind).toBe(SymbolKind.Interface);

      // Protocol requirements
      const protocolDraw = symbols.find(s => s.name === 'draw' && s.parentId === drawable?.id);
      expect(protocolDraw).toBeDefined();
      expect(protocolDraw?.kind).toBe(SymbolKind.Method);

      const protocolArea = symbols.find(s => s.name === 'area' && s.parentId === drawable?.id);
      expect(protocolArea).toBeDefined();
      expect(protocolArea?.signature).toContain('{ get }');

      const defaultColor = symbols.find(s => s.name === 'defaultColor');
      expect(defaultColor).toBeDefined();
      expect(defaultColor?.signature).toContain('static var');
      expect(defaultColor?.signature).toContain('{ get set }');

      // Multiple protocol conformance
      const circle = symbols.find(s => s.name === 'Circle');
      expect(circle).toBeDefined();
      expect(circle?.signature).toContain('Drawable, Named');

      // Extension extraction
      const circleExtension = symbols.find(s => s.name === 'Circle' && s.signature?.includes('extension'));
      expect(circleExtension).toBeDefined();

      // Extension methods
      const convenience = symbols.find(s => s.name === 'init' && s.signature?.includes('convenience'));
      expect(convenience).toBeDefined();
      expect(convenience?.signature).toContain('convenience init');

      const circumference = symbols.find(s => s.name === 'circumference');
      expect(circumference).toBeDefined();
    });
  });

  describe('Enum and Associated Values', () => {
    it('should extract enums with cases and associated values', async () => {
      const swiftCode = `
enum Direction {
    case north
    case south
    case east
    case west
}

enum Result<T> {
    case success(T)
    case failure(Error)
    case pending
}

indirect enum Expression {
    case number(Int)
    case addition(Expression, Expression)
    case multiplication(Expression, Expression)
}

enum HTTPStatusCode: Int, CaseIterable {
    case ok = 200
    case notFound = 404
    case internalServerError = 500

    var description: String {
        switch self {
        case .ok: return "OK"
        case .notFound: return "Not Found"
        case .internalServerError: return "Internal Server Error"
        }
    }
}
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Simple enum
      const direction = symbols.find(s => s.name === 'Direction');
      expect(direction).toBeDefined();
      expect(direction?.kind).toBe(SymbolKind.Enum);

      // Enum cases
      const north = symbols.find(s => s.name === 'north');
      expect(north).toBeDefined();
      expect(north?.kind).toBe(SymbolKind.EnumMember);

      // Generic enum with associated values
      const resultSymbol = symbols.find(s => s.name === 'Result');
      expect(resultSymbol).toBeDefined();
      expect(resultSymbol?.signature).toContain('enum Result<T>');

      const success = symbols.find(s => s.name === 'success');
      expect(success).toBeDefined();
      expect(success?.signature).toContain('success(T)');

      // Indirect enum
      const expression = symbols.find(s => s.name === 'Expression');
      expect(expression).toBeDefined();
      expect(expression?.signature).toContain('indirect enum');

      // Enum with raw values and protocol conformance
      const httpStatus = symbols.find(s => s.name === 'HTTPStatusCode');
      expect(httpStatus).toBeDefined();
      expect(httpStatus?.signature).toContain(': Int, CaseIterable');

      const ok = symbols.find(s => s.name === 'ok');
      expect(ok).toBeDefined();
      expect(ok?.signature).toContain('= 200');

      // Computed property in enum
      const description = symbols.find(s => s.name === 'description' && s.parentId === httpStatus?.id);
      expect(description).toBeDefined();
      expect(description?.signature).toContain('var description: String');
    });
  });

  describe('Generics and Type Constraints', () => {
    it('should extract generic types and functions with constraints', async () => {
      const swiftCode = `
struct Stack<Element> {
    private var items: [Element] = []

    mutating func push(_ item: Element) {
        items.append(item)
    }

    mutating func pop() -> Element? {
        return items.isEmpty ? nil : items.removeLast()
    }
}

func swapValues<T>(_ a: inout T, _ b: inout T) {
    let temp = a
    a = b
    b = temp
}

func findIndex<T: Equatable>(of valueToFind: T, in array: [T]) -> Int? {
    for (index, value) in array.enumerated() {
        if value == valueToFind {
            return index
        }
    }
    return nil
}

class Container<Item> where Item: Equatable {
    var items: [Item] = []

    func add(_ item: Item) {
        items.append(item)
    }

    func contains(_ item: Item) -> Bool {
        return items.contains(item)
    }
}

protocol Container {
    associatedtype Item
    var count: Int { get }
    mutating func append(_ item: Item)
    subscript(i: Int) -> Item { get }
}
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Generic struct
      const stack = symbols.find(s => s.name === 'Stack');
      expect(stack).toBeDefined();
      expect(stack?.signature).toContain('Stack<Element>');

      // Generic function
      const swapValues = symbols.find(s => s.name === 'swapValues');
      expect(swapValues).toBeDefined();
      expect(swapValues?.signature).toContain('func swapValues<T>');
      expect(swapValues?.signature).toContain('inout T');

      // Generic function with type constraint
      const findIndex = symbols.find(s => s.name === 'findIndex');
      expect(findIndex).toBeDefined();
      expect(findIndex?.signature).toContain('<T: Equatable>');

      // Generic class with where clause
      const container = symbols.find(s => s.name === 'Container' && s.kind === SymbolKind.Class);
      expect(container).toBeDefined();
      expect(container?.signature).toContain('where Item: Equatable');

      // Associated type in protocol
      const containerProtocol = symbols.find(s => s.name === 'Container' && s.kind === SymbolKind.Interface);
      expect(containerProtocol).toBeDefined();

      const associatedType = symbols.find(s => s.name === 'Item' && s.kind === SymbolKind.Type);
      expect(associatedType).toBeDefined();
      expect(associatedType?.signature).toContain('associatedtype Item');

      // Subscript
      const subscriptMethod = symbols.find(s => s.name === 'subscript');
      expect(subscriptMethod).toBeDefined();
      expect(subscriptMethod?.signature).toContain('subscript(i: Int) -> Item');
    });
  });

  describe('Closures and Function Types', () => {
    it('should extract closures and function type properties', async () => {
      const swiftCode = `
class EventHandler {
    var onComplete: (() -> Void)?
    var onSuccess: ((String) -> Void)?
    var onError: ((Error) -> Void)?
    var transformer: ((Int) -> String) = { number in
        return "Number: \\(number)"
    }

    func processAsync(completion: @escaping (Result<String, Error>) -> Void) {
        // Async processing
    }

    lazy var expensiveComputation: () -> String = {
        return "Computed result"
    }()
}

func performOperation<T, U>(
    input: T,
    transform: (T) throws -> U,
    completion: @escaping (Result<U, Error>) -> Void
) {
    do {
        let result = try transform(input)
        completion(.success(result))
    } catch {
        completion(.failure(error))
    }
}

typealias CompletionHandler = (Bool, Error?) -> Void
typealias GenericHandler<T> = (T?, Error?) -> Void
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Function type properties
      const onComplete = symbols.find(s => s.name === 'onComplete');
      expect(onComplete).toBeDefined();
      expect(onComplete?.signature).toContain('(() -> Void)?');

      const onSuccess = symbols.find(s => s.name === 'onSuccess');
      expect(onSuccess).toBeDefined();
      expect(onSuccess?.signature).toContain('((String) -> Void)?');

      // Property with closure default value
      const transformer = symbols.find(s => s.name === 'transformer');
      expect(transformer).toBeDefined();
      expect(transformer?.signature).toContain('((Int) -> String)');

      // Method with escaping closure
      const processAsync = symbols.find(s => s.name === 'processAsync');
      expect(processAsync).toBeDefined();
      expect(processAsync?.signature).toContain('@escaping');
      expect(processAsync?.signature).toContain('(Result<String, Error>) -> Void');

      // Lazy property
      const expensiveComputation = symbols.find(s => s.name === 'expensiveComputation');
      expect(expensiveComputation).toBeDefined();
      expect(expensiveComputation?.signature).toContain('lazy var');

      // Function with throwing closure
      const performOperation = symbols.find(s => s.name === 'performOperation');
      expect(performOperation).toBeDefined();
      expect(performOperation?.signature).toContain('throws ->');

      // Type aliases
      const completionHandler = symbols.find(s => s.name === 'CompletionHandler');
      expect(completionHandler).toBeDefined();
      expect(completionHandler?.kind).toBe(SymbolKind.Type);
      expect(completionHandler?.signature).toContain('typealias CompletionHandler');

      const genericHandler = symbols.find(s => s.name === 'GenericHandler');
      expect(genericHandler).toBeDefined();
      expect(genericHandler?.signature).toContain('typealias GenericHandler<T>');
    });
  });

  describe('Property Wrappers and Attributes', () => {
    it('should extract property wrappers and compiler attributes', async () => {
      const swiftCode = `
@propertyWrapper
struct UserDefault<T> {
    let key: String
    let defaultValue: T

    var wrappedValue: T {
        get {
            UserDefaults.standard.object(forKey: key) as? T ?? defaultValue
        }
        set {
            UserDefaults.standard.set(newValue, forKey: key)
        }
    }
}

class SettingsManager {
    @UserDefault(key: "username", defaultValue: "")
    var username: String

    @UserDefault(key: "isFirstLaunch", defaultValue: true)
    var isFirstLaunch: Bool

    @Published var currentTheme: Theme = .light

    @objc dynamic var observableProperty: String = ""

    @available(iOS 13.0, *)
    func modernFunction() {
        // iOS 13+ only
    }

    @discardableResult
    func processData() -> Bool {
        return true
    }
}

@frozen
struct Point3D {
    let x: Double
    let y: Double
    let z: Double
}

@main
struct MyApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
        }
    }
}
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Property wrapper struct
      const userDefault = symbols.find(s => s.name === 'UserDefault');
      expect(userDefault).toBeDefined();
      expect(userDefault?.signature).toContain('@propertyWrapper');

      // Property with wrapper
      const username = symbols.find(s => s.name === 'username');
      expect(username).toBeDefined();
      expect(username?.signature).toContain('@UserDefault');

      // Published property
      const currentTheme = symbols.find(s => s.name === 'currentTheme');
      expect(currentTheme).toBeDefined();
      expect(currentTheme?.signature).toContain('@Published');

      // Objective-C interop
      const observableProperty = symbols.find(s => s.name === 'observableProperty');
      expect(observableProperty).toBeDefined();
      expect(observableProperty?.signature).toContain('@objc dynamic');

      // Availability attribute
      const modernFunction = symbols.find(s => s.name === 'modernFunction');
      expect(modernFunction).toBeDefined();
      expect(modernFunction?.signature).toContain('@available(iOS 13.0, *)');

      // Discardable result
      const processData = symbols.find(s => s.name === 'processData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('@discardableResult');

      // Frozen struct
      const point3D = symbols.find(s => s.name === 'Point3D');
      expect(point3D).toBeDefined();
      expect(point3D?.signature).toContain('@frozen');

      // Main attribute
      const myApp = symbols.find(s => s.name === 'MyApp');
      expect(myApp).toBeDefined();
      expect(myApp?.signature).toContain('@main');
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer types from Swift type annotations and declarations', async () => {
      const swiftCode = `
class DataProcessor {
    func processString(_ input: String) -> String {
        return input.uppercased()
    }

    func processNumbers(_ numbers: [Int]) -> Double {
        return numbers.reduce(0, +) / Double(numbers.count)
    }

    var configuration: [String: Any] = [:]
    let processor: (String) -> String = { $0.lowercased() }
}

protocol DataSource {
    associatedtype Element
    func fetch() -> [Element]
}

class NetworkDataSource: DataSource {
    typealias Element = NetworkResponse

    func fetch() -> [NetworkResponse] {
        return []
    }
}
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      // Function return types
      const processString = symbols.find(s => s.name === 'processString');
      expect(processString).toBeDefined();
      expect(types.get(processString!.id)).toBe('String');

      const processNumbers = symbols.find(s => s.name === 'processNumbers');
      expect(processNumbers).toBeDefined();
      expect(types.get(processNumbers!.id)).toBe('Double');

      // Property types
      const configuration = symbols.find(s => s.name === 'configuration');
      expect(configuration).toBeDefined();
      expect(types.get(configuration!.id)).toBe('[String: Any]');

      const processor = symbols.find(s => s.name === 'processor');
      expect(processor).toBeDefined();
      expect(types.get(processor!.id)).toBe('(String) -> String');
    });

    it('should extract inheritance and protocol conformance relationships', async () => {
      const swiftCode = `
protocol Vehicle {
    var speed: Double { get set }
    func start()
}

protocol Electric {
    var batteryLevel: Double { get }
}

class Car: Vehicle {
    var speed: Double = 0

    func start() {
        print("Car started")
    }
}

class Tesla: Car, Electric {
    var batteryLevel: Double = 100.0

    override func start() {
        super.start()
        print("Tesla started silently")
    }
}
`;

      const result = await parserManager.parseFile('test.swift', swiftCode);
      const extractor = new SwiftExtractor('swift', 'test.swift', swiftCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance and protocol conformance relationships
      expect(relationships.length).toBeGreaterThanOrEqual(3);

      // Car implements Vehicle
      const carVehicle = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Car' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Vehicle'
      );
      expect(carVehicle).toBeDefined();

      // Tesla extends Car
      const teslaExtendsCar = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Tesla' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Car'
      );
      expect(teslaExtendsCar).toBeDefined();

      // Tesla implements Electric
      const teslaElectric = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Tesla' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Electric'
      );
      expect(teslaElectric).toBeDefined();
    });
  });
});