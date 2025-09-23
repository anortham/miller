import { describe, it, expect, beforeAll } from 'bun:test';
import { CppExtractor } from '../../extractors/cpp-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('CppExtractor', () => {
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

  describe('Namespace and Include Extraction', () => {
    it('should extract namespace declarations and include statements', async () => {
      const cppCode = `
#include <iostream>
#include <vector>
#include "custom_header.h"

using namespace std;
using std::string;

namespace MyCompany {
    namespace Utils {
        // Nested namespace content
    }
}

namespace MyProject = MyCompany::Utils;  // Namespace alias
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const stdNamespace = symbols.find(s => s.name === 'std');
      expect(stdNamespace).toBeDefined();
      expect(stdNamespace?.kind).toBe(SymbolKind.Import);

      const myCompany = symbols.find(s => s.name === 'MyCompany');
      expect(myCompany).toBeDefined();
      expect(myCompany?.kind).toBe(SymbolKind.Namespace);

      const utils = symbols.find(s => s.name === 'Utils');
      expect(utils).toBeDefined();
      expect(utils?.kind).toBe(SymbolKind.Namespace);

      const alias = symbols.find(s => s.name === 'MyProject');
      expect(alias).toBeDefined();
      expect(alias?.signature).toContain('MyCompany::Utils');
    });
  });

  describe('Class Extraction', () => {
    it('should extract class declarations with inheritance and access specifiers', async () => {
      const cppCode = `
namespace Geometry {
    class Shape {
    public:
        virtual ~Shape() = default;
        virtual double area() const = 0;
        virtual void draw() const;

    protected:
        std::string name_;

    private:
        int id_;
    };

    class Circle : public Shape {
    public:
        Circle(double radius);
        double area() const override;

        static int getInstanceCount();

    private:
        double radius_;
        static int instance_count_;
    };

    class Rectangle : public Shape {
    public:
        Rectangle(double width, double height) : width_(width), height_(height) {}
        double area() const override { return width_ * height_; }

    private:
        double width_, height_;
    };

    // Multiple inheritance
    class Drawable {
    public:
        virtual void render() = 0;
    };

    class ColoredCircle : public Circle, public Drawable {
    public:
        ColoredCircle(double radius, const std::string& color);
        void render() override;

    private:
        std::string color_;
    };
}
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const shape = symbols.find(s => s.name === 'Shape');
      expect(shape).toBeDefined();
      expect(shape?.kind).toBe(SymbolKind.Class);
      expect(shape?.signature).toContain('class Shape');

      const circle = symbols.find(s => s.name === 'Circle');
      expect(circle).toBeDefined();
      expect(circle?.signature).toContain('public Shape');

      const coloredCircle = symbols.find(s => s.name === 'ColoredCircle');
      expect(coloredCircle).toBeDefined();
      expect(coloredCircle?.signature).toContain('public Circle, public Drawable');

      // Check methods
      const destructor = symbols.find(s => s.name === '~Shape');
      expect(destructor).toBeDefined();
      expect(destructor?.kind).toBe(SymbolKind.Destructor);

      const area = symbols.find(s => s.name === 'area');
      expect(area).toBeDefined();
      expect(area?.kind).toBe(SymbolKind.Method);
      expect(area?.signature).toContain('virtual');

      const getInstanceCount = symbols.find(s => s.name === 'getInstanceCount');
      expect(getInstanceCount).toBeDefined();
      expect(getInstanceCount?.signature).toContain('static');
    });
  });

  describe('Template Extraction', () => {
    it('should extract template classes and functions', async () => {
      const cppCode = `
template<typename T>
class Vector {
public:
    Vector(size_t size);
    void push_back(const T& item);
    T& operator[](size_t index);

private:
    T* data_;
    size_t size_;
    size_t capacity_;
};

template<typename T, size_t N>
class Array {
public:
    T& at(size_t index) { return data_[index]; }

private:
    T data_[N];
};

template<typename T>
T max(const T& a, const T& b) {
    return (a > b) ? a : b;
}

template<typename T, typename U>
auto add(T a, U b) -> decltype(a + b) {
    return a + b;
}

// Template specialization
template<>
class Vector<bool> {
public:
    void flip();
private:
    std::vector<uint8_t> data_;
};
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const vector = symbols.find(s => s.name === 'Vector');
      expect(vector).toBeDefined();
      expect(vector?.signature).toContain('template<typename T>');
      expect(vector?.signature).toContain('class Vector');

      const array = symbols.find(s => s.name === 'Array');
      expect(array).toBeDefined();
      expect(array?.signature).toContain('template<typename T, size_t N>');

      const maxFunc = symbols.find(s => s.name === 'max');
      expect(maxFunc).toBeDefined();
      expect(maxFunc?.kind).toBe(SymbolKind.Function);
      expect(maxFunc?.signature).toContain('template<typename T>');

      const addFunc = symbols.find(s => s.name === 'add');
      expect(addFunc).toBeDefined();
      expect(addFunc?.signature).toContain('auto add(T a, U b) -> decltype(a + b)');

      const vectorBool = symbols.find(s => s.name === 'Vector' && s.signature?.includes('<bool>'));
      expect(vectorBool).toBeDefined();
    });
  });

  describe('Function and Operator Extraction', () => {
    it('should extract functions and operator overloads', async () => {
      const cppCode = `
int factorial(int n);

inline double square(double x) {
    return x * x;
}

class Complex {
public:
    Complex(double real = 0, double imag = 0);

    // Arithmetic operators
    Complex operator+(const Complex& other) const;
    Complex operator-(const Complex& other) const;
    Complex& operator+=(const Complex& other);

    // Comparison operators
    bool operator==(const Complex& other) const;
    bool operator!=(const Complex& other) const;

    // Stream operators
    friend std::ostream& operator<<(std::ostream& os, const Complex& c);
    friend std::istream& operator>>(std::istream& is, Complex& c);

    // Conversion operators
    operator double() const;
    explicit operator bool() const;

    // Function call operator
    double operator()(double x) const;

    // Subscript operator
    double& operator[](int index);

private:
    double real_, imag_;
};

// Global operators
Complex operator*(const Complex& a, const Complex& b);
Complex operator/(const Complex& a, const Complex& b);
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const factorial = symbols.find(s => s.name === 'factorial');
      expect(factorial).toBeDefined();
      expect(factorial?.kind).toBe(SymbolKind.Function);

      const square = symbols.find(s => s.name === 'square');
      expect(square).toBeDefined();
      expect(square?.signature).toContain('inline');

      const plusOp = symbols.find(s => s.name === 'operator+');
      expect(plusOp).toBeDefined();
      expect(plusOp?.kind).toBe(SymbolKind.Operator);
      expect(plusOp?.signature).toContain('operator+');

      const streamOp = symbols.find(s => s.name === 'operator<<');
      expect(streamOp).toBeDefined();
      expect(streamOp?.signature).toContain('friend');

      const conversionOp = symbols.find(s => s.name === 'operator double');
      expect(conversionOp).toBeDefined();

      const callOp = symbols.find(s => s.name === 'operator()');
      expect(callOp).toBeDefined();
    });
  });

  describe('Struct and Union Extraction', () => {
    it('should extract struct and union declarations', async () => {
      const cppCode = `
struct Point {
    double x, y;

    Point(double x = 0, double y = 0) : x(x), y(y) {}

    double distance() const {
        return sqrt(x * x + y * y);
    }
};

struct alignas(16) AlignedData {
    float data[4];
};

union Value {
    int i;
    float f;
    double d;
    char c[8];

    Value() : i(0) {}
    Value(int val) : i(val) {}
    Value(float val) : f(val) {}
};

// Anonymous union
struct Variant {
    enum Type { INT, FLOAT, STRING } type;

    union {
        int int_val;
        float float_val;
        std::string* string_val;
    };

    Variant(int val) : type(INT), int_val(val) {}
};
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const point = symbols.find(s => s.name === 'Point');
      expect(point).toBeDefined();
      expect(point?.kind).toBe(SymbolKind.Struct);
      expect(point?.signature).toContain('struct Point');

      const alignedData = symbols.find(s => s.name === 'AlignedData');
      expect(alignedData).toBeDefined();
      expect(alignedData?.signature).toContain('alignas(16)');

      const value = symbols.find(s => s.name === 'Value');
      expect(value).toBeDefined();
      expect(value?.kind).toBe(SymbolKind.Union);

      const distance = symbols.find(s => s.name === 'distance');
      expect(distance).toBeDefined();
      expect(distance?.kind).toBe(SymbolKind.Method);

      const variant = symbols.find(s => s.name === 'Variant');
      expect(variant).toBeDefined();
      expect(variant?.kind).toBe(SymbolKind.Struct);
    });
  });

  describe('Enum Extraction', () => {
    it('should extract enums and scoped enums', async () => {
      const cppCode = `
enum Color {
    RED,
    GREEN,
    BLUE,
    ALPHA = 255
};

enum class Status : uint8_t {
    Pending = 1,
    Active = 2,
    Inactive = 3,
    Error = 0xFF
};

enum Direction { NORTH, SOUTH, EAST, WEST };

// Anonymous enum
enum {
    MAX_BUFFER_SIZE = 1024,
    DEFAULT_TIMEOUT = 30
};
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const color = symbols.find(s => s.name === 'Color');
      expect(color).toBeDefined();
      expect(color?.kind).toBe(SymbolKind.Enum);
      expect(color?.signature).toContain('enum Color');

      const status = symbols.find(s => s.name === 'Status');
      expect(status).toBeDefined();
      expect(status?.signature).toContain('enum class Status : uint8_t');

      const red = symbols.find(s => s.name === 'RED');
      expect(red).toBeDefined();
      expect(red?.kind).toBe(SymbolKind.EnumMember);

      const pending = symbols.find(s => s.name === 'Pending');
      expect(pending).toBeDefined();
      expect(pending?.signature).toContain('= 1');

      const alpha = symbols.find(s => s.name === 'ALPHA');
      expect(alpha).toBeDefined();
      expect(alpha?.signature).toContain('= 255');

      const maxBufferSize = symbols.find(s => s.name === 'MAX_BUFFER_SIZE');
      expect(maxBufferSize).toBeDefined();
      expect(maxBufferSize?.kind).toBe(SymbolKind.Constant);
    });
  });

  describe('Constructor and Destructor Extraction', () => {
    it('should extract constructors and destructors with various patterns', async () => {
      const cppCode = `
class Resource {
public:
    // Default constructor
    Resource();

    // Parameterized constructor
    Resource(const std::string& name, size_t size);

    // Copy constructor
    Resource(const Resource& other);

    // Move constructor
    Resource(Resource&& other) noexcept;

    // Delegating constructor
    Resource(const std::string& name) : Resource(name, 0) {}

    // Copy assignment operator
    Resource& operator=(const Resource& other);

    // Move assignment operator
    Resource& operator=(Resource&& other) noexcept;

    // Destructor
    virtual ~Resource();

    // Deleted functions
    Resource(int) = delete;
    void forbidden() = delete;

private:
    std::string name_;
    size_t size_;
    void* data_;
};

class AutoResource {
public:
    AutoResource() = default;
    ~AutoResource() = default;

    AutoResource(const AutoResource&) = delete;
    AutoResource& operator=(const AutoResource&) = delete;
};
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const constructors = symbols.filter(s => s.kind === SymbolKind.Constructor);
      expect(constructors.length).toBeGreaterThanOrEqual(5);

      const defaultCtor = constructors.find(s => s.signature?.includes('Resource()'));
      expect(defaultCtor).toBeDefined();

      const paramCtor = constructors.find(s => s.signature?.includes('std::string& name, size_t size'));
      expect(paramCtor).toBeDefined();

      const copyCtor = constructors.find(s => s.signature?.includes('const Resource& other'));
      expect(copyCtor).toBeDefined();

      const moveCtor = constructors.find(s => s.signature?.includes('Resource&& other'));
      expect(moveCtor).toBeDefined();
      expect(moveCtor?.signature).toContain('noexcept');

      const destructor = symbols.find(s => s.kind === SymbolKind.Destructor);
      expect(destructor).toBeDefined();
      expect(destructor?.name).toBe('~Resource');
      expect(destructor?.signature).toContain('virtual');

      const deletedCtor = constructors.find(s => s.signature?.includes('= delete'));
      expect(deletedCtor).toBeDefined();

      const defaultedCtor = symbols.find(s => s.signature?.includes('= default'));
      expect(defaultedCtor).toBeDefined();
    });
  });

  describe('Variable and Constant Extraction', () => {
    it('should extract variables and constants with various storage classes', async () => {
      const cppCode = `
// Global variables
int global_var = 42;
const double PI = 3.14159;
static int static_var = 0;
extern int extern_var;

namespace Constants {
    constexpr int MAX_SIZE = 1000;
    inline constexpr double E = 2.71828;
    const char* const VERSION = "1.0.0";
}

class DataHolder {
public:
    static const int CLASS_CONSTANT = 100;
    static constexpr double PRECISION = 1e-9;

    mutable int cache_hits_;

private:
    static int instance_count_;
    volatile bool running_;
    std::atomic<int> counter_;

    // Static member initialization
    static inline std::string default_name_ = "unnamed";
};

// Thread-local storage
thread_local int tls_var = 0;

// Register hint
register int fast_counter = 0;
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const globalVar = symbols.find(s => s.name === 'global_var');
      expect(globalVar).toBeDefined();
      expect(globalVar?.kind).toBe(SymbolKind.Variable);

      const pi = symbols.find(s => s.name === 'PI');
      expect(pi).toBeDefined();
      expect(pi?.kind).toBe(SymbolKind.Constant);
      expect(pi?.signature).toContain('const double');

      const staticVar = symbols.find(s => s.name === 'static_var');
      expect(staticVar).toBeDefined();
      expect(staticVar?.signature).toContain('static');

      const externVar = symbols.find(s => s.name === 'extern_var');
      expect(externVar).toBeDefined();
      expect(externVar?.signature).toContain('extern');

      const maxSize = symbols.find(s => s.name === 'MAX_SIZE');
      expect(maxSize).toBeDefined();
      expect(maxSize?.signature).toContain('constexpr');

      const classConstant = symbols.find(s => s.name === 'CLASS_CONSTANT');
      expect(classConstant).toBeDefined();
      expect(classConstant?.signature).toContain('static const');

      const tlsVar = symbols.find(s => s.name === 'tls_var');
      expect(tlsVar).toBeDefined();
      expect(tlsVar?.signature).toContain('thread_local');
    });
  });

  describe('Friend and Access Specifier Extraction', () => {
    it('should handle friend declarations and access specifiers', async () => {
      const cppCode = `
class Matrix;  // Forward declaration

class Vector {
private:
    double* data_;
    size_t size_;

public:
    Vector(size_t size);

    // Friend function
    friend Vector operator+(const Vector& a, const Vector& b);

    // Friend class
    friend class Matrix;

    // Friend member function
    friend void Matrix::multiply(const Vector& v);

protected:
    void resize(size_t new_size);

private:
    void deallocate();
};

class Matrix {
public:
    void multiply(const Vector& v);

private:
    double** data_;
    size_t rows_, cols_;
};
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const vector = symbols.find(s => s.name === 'Vector');
      expect(vector).toBeDefined();

      const friendOp = symbols.find(s => s.name === 'operator+');
      expect(friendOp).toBeDefined();
      expect(friendOp?.signature).toContain('friend');

      const resize = symbols.find(s => s.name === 'resize');
      expect(resize).toBeDefined();
      expect(resize?.visibility).toBe('protected');

      const deallocate = symbols.find(s => s.name === 'deallocate');
      expect(deallocate).toBeDefined();
      expect(deallocate?.visibility).toBe('private');

      const multiply = symbols.find(s => s.name === 'multiply');
      expect(multiply).toBeDefined();
      expect(multiply?.visibility).toBe('public');
    });
  });

  describe('Type Inference', () => {
    it('should infer types from C++ type annotations and auto', async () => {
      const cppCode = `
class TypeExample {
public:
    int getValue() const { return 42; }
    std::vector<std::string> getNames() const;

    template<typename T>
    T process(const T& input) const { return input; }

    auto getAuto() -> int { return 0; }
    auto getLambda() { return [](int x) { return x * 2; }; }
};
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      const getValue = symbols.find(s => s.name === 'getValue');
      expect(getValue).toBeDefined();
      expect(types.get(getValue!.id)).toBe('int');

      const getNames = symbols.find(s => s.name === 'getNames');
      expect(getNames).toBeDefined();
      expect(types.get(getNames!.id)).toBe('std::vector<std::string>');
    });
  });

  describe('Relationship Extraction', () => {
    it('should extract inheritance and template relationships', async () => {
      const cppCode = `
class Base {
public:
    virtual void method() = 0;
};

class Derived : public Base {
public:
    void method() override;
};

template<typename T>
class Container : public Base {
public:
    void add(const T& item);
};

class IntContainer : public Container<int> {
public:
    void addMultiple(const std::vector<int>& items);
};
`;

      const result = await parserManager.parseFile('test.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'test.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance relationships
      expect(relationships.length).toBeGreaterThanOrEqual(1);

      const inheritance = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromId)?.name === 'Derived' &&
        symbols.find(s => s.id === r.toId)?.name === 'Base'
      );
      expect(inheritance).toBeDefined();
    });
  });

  describe('Modern C++ Features', () => {
    it('should extract lambdas, smart pointers, and modern C++ constructs', async () => {
      const cppCode = `
#include <memory>
#include <functional>
#include <algorithm>
#include <ranges>
#include <concepts>

namespace ModernCpp {
    // Smart pointers
    class ResourceManager {
    public:
        std::unique_ptr<int[]> createArray(size_t size) {
            return std::make_unique<int[]>(size);
        }

        std::shared_ptr<std::string> createSharedString(const std::string& value) {
            return std::make_shared<std::string>(value);
        }

        std::weak_ptr<Resource> getWeakRef(std::shared_ptr<Resource> res) {
            return std::weak_ptr<Resource>(res);
        }
    };

    // Lambda expressions
    class LambdaExamples {
    public:
        void processData() {
            std::vector<int> numbers = {1, 2, 3, 4, 5};

            // Simple lambda
            auto square = [](int x) { return x * x; };

            // Lambda with capture
            int multiplier = 10;
            auto multiply = [multiplier](int x) { return x * multiplier; };

            // Lambda with capture by reference
            auto increment = [&multiplier](int x) { return x + ++multiplier; };

            // Generic lambda (C++14)
            auto genericLambda = [](auto a, auto b) { return a + b; };

            // Mutable lambda
            auto counter = [count = 0]() mutable { return ++count; };

            // Lambda with trailing return type
            auto complexLambda = [](double x) -> std::pair<double, double> {
                return {x, x * x};
            };

            // Lambda in algorithm
            std::for_each(numbers.begin(), numbers.end(), [](int& n) { n *= 2; });
        }

        template<typename Func>
        void applyFunction(const std::vector<int>& data, Func func) {
            std::for_each(data.begin(), data.end(), func);
        }
    };

    // C++20 Concepts
#ifdef __cpp_concepts
    template<typename T>
    concept Numeric = std::integral<T> || std::floating_point<T>;

    template<Numeric T>
    T add(T a, T b) {
        return a + b;
    }

    template<typename T>
    concept Container = requires(T t) {
        t.begin();
        t.end();
        t.size();
    };

    template<Container C>
    auto processContainer(const C& container) {
        return std::distance(container.begin(), container.end());
    }
#endif

    // C++20 Ranges
#ifdef __cpp_lib_ranges
    class RangeExamples {
    public:
        void demonstrateRanges() {
            std::vector<int> numbers = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10};

            // Range-based transformations
            auto even_squares = numbers
                | std::views::filter([](int n) { return n % 2 == 0; })
                | std::views::transform([](int n) { return n * n; });

            auto first_three = numbers | std::views::take(3);
        }
    };
#endif

    // Structured bindings (C++17)
    class StructuredBindings {
    public:
        std::pair<int, std::string> getData() {
            return {42, "hello"};
        }

        void useStructuredBindings() {
            auto [number, text] = getData();
            auto [x, y, z] = std::make_tuple(1, 2.5, "test");
        }
    };

    // constexpr and consteval (C++20)
    class CompileTimeComputation {
    public:
        static constexpr int factorial(int n) {
            return (n <= 1) ? 1 : n * factorial(n - 1);
        }

#ifdef __cpp_consteval
        static consteval int fibonacci(int n) {
            return (n <= 1) ? n : fibonacci(n - 1) + fibonacci(n - 2);
        }
#endif

        static constinit int global_constant = factorial(5);
    };

    // Move semantics and perfect forwarding
    template<typename T>
    class MoveSemantics {
    private:
        T data_;

    public:
        // Perfect forwarding constructor
        template<typename U>
        explicit MoveSemantics(U&& value) : data_(std::forward<U>(value)) {}

        // Move constructor
        MoveSemantics(MoveSemantics&& other) noexcept
            : data_(std::move(other.data_)) {}

        // Move assignment operator
        MoveSemantics& operator=(MoveSemantics&& other) noexcept {
            if (this != &other) {
                data_ = std::move(other.data_);
            }
            return *this;
        }

        T&& moveData() && { return std::move(data_); }
        const T& getData() const& { return data_; }
    };
}
`;

      const result = await parserManager.parseFile('modern.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'modern.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const resourceManager = symbols.find(s => s.name === 'ResourceManager');
      expect(resourceManager).toBeDefined();
      expect(resourceManager?.kind).toBe(SymbolKind.Class);

      const createArray = symbols.find(s => s.name === 'createArray');
      expect(createArray).toBeDefined();
      expect(createArray?.signature).toContain('std::unique_ptr<int[]>');

      const createSharedString = symbols.find(s => s.name === 'createSharedString');
      expect(createSharedString).toBeDefined();
      expect(createSharedString?.signature).toContain('std::shared_ptr<std::string>');

      const lambdaExamples = symbols.find(s => s.name === 'LambdaExamples');
      expect(lambdaExamples).toBeDefined();

      const processData = symbols.find(s => s.name === 'processData');
      expect(processData).toBeDefined();
      expect(processData?.kind).toBe(SymbolKind.Method);

      const applyFunction = symbols.find(s => s.name === 'applyFunction');
      expect(applyFunction).toBeDefined();
      expect(applyFunction?.signature).toContain('template<typename Func>');

      const addFunction = symbols.find(s => s.name === 'add');
      if (addFunction) {
        expect(addFunction?.signature).toContain('Numeric T');
      }

      const structuredBindings = symbols.find(s => s.name === 'StructuredBindings');
      expect(structuredBindings).toBeDefined();

      const getData = symbols.find(s => s.name === 'getData');
      expect(getData).toBeDefined();
      expect(getData?.signature).toContain('std::pair<int, std::string>');

      const compileTimeComputation = symbols.find(s => s.name === 'CompileTimeComputation');
      expect(compileTimeComputation).toBeDefined();

      const factorial = symbols.find(s => s.name === 'factorial');
      expect(factorial).toBeDefined();
      expect(factorial?.signature).toContain('constexpr');

      const moveSemantics = symbols.find(s => s.name === 'MoveSemantics');
      expect(moveSemantics).toBeDefined();
      expect(moveSemantics?.signature).toContain('template<typename T>');

      const moveConstructor = symbols.filter(s => s.kind === SymbolKind.Constructor && s.signature?.includes('&&'));
      expect(moveConstructor.length).toBeGreaterThanOrEqual(1);

      const moveData = symbols.find(s => s.name === 'moveData');
      expect(moveData).toBeDefined();
      expect(moveData?.signature).toContain('&&');
    });
  });

  describe('Advanced Template Features', () => {
    it('should extract variadic templates, SFINAE, and template metaprogramming', async () => {
      const cppCode = `
#include <type_traits>
#include <utility>

namespace AdvancedTemplates {
    // Variadic templates
    template<typename... Args>
    class VariadicTuple {
    private:
        std::tuple<Args...> data_;

    public:
        template<typename... UArgs>
        explicit VariadicTuple(UArgs&&... args)
            : data_(std::forward<UArgs>(args)...) {}

        template<std::size_t I>
        auto get() -> decltype(std::get<I>(data_)) {
            return std::get<I>(data_);
        }

        static constexpr std::size_t size() {
            return sizeof...(Args);
        }
    };

    // Variadic function template
    template<typename T>
    T sum(T&& value) {
        return std::forward<T>(value);
    }

    template<typename T, typename... Args>
    T sum(T&& first, Args&&... rest) {
        return std::forward<T>(first) + sum(std::forward<Args>(rest)...);
    }

    // SFINAE and enable_if
    template<typename T>
    class SFINAEExamples {
    public:
        // Enable if T is integral
        template<typename U = T>
        typename std::enable_if_t<std::is_integral_v<U>, U>
        processIntegral(U value) {
            return value * 2;
        }

        // Enable if T is floating point
        template<typename U = T>
        typename std::enable_if_t<std::is_floating_point_v<U>, U>
        processFloating(U value) {
            return value * 1.5;
        }

        // SFINAE with decltype
        template<typename U>
        auto hasBegin(U&& container) -> decltype(container.begin(), std::true_type{});
        std::false_type hasBegin(...);

        // if constexpr (C++17)
        template<typename U>
        void processType(U&& value) {
            if constexpr (std::is_same_v<std::decay_t<U>, std::string>) {
                // Handle string
            } else if constexpr (std::is_integral_v<U>) {
                // Handle integral types
            } else {
                // Handle other types
            }
        }
    };

    // Template specialization
    template<typename T, std::size_t N>
    class Array {
    private:
        T data_[N];

    public:
        T& operator[](std::size_t index) { return data_[index]; }
        const T& operator[](std::size_t index) const { return data_[index]; }
        constexpr std::size_t size() const { return N; }
    };

    // Partial specialization for pointer types
    template<typename T>
    class Array<T*, 0> {
    public:
        void doNothing() {}
    };

    // Full specialization for bool
    template<std::size_t N>
    class Array<bool, N> {
    private:
        std::bitset<N> bits_;

    public:
        bool operator[](std::size_t index) const { return bits_[index]; }
        void set(std::size_t index, bool value) { bits_[index] = value; }
    };

    // Template template parameters
    template<template<typename> class Container, typename T>
    class TemplateTemplateExample {
    private:
        Container<T> container_;

    public:
        void add(const T& item) {
            container_.push_back(item);
        }

        template<typename Func>
        void forEach(Func func) {
            for (auto& item : container_) {
                func(item);
            }
        }
    };

    // Type traits and metaprogramming
    template<typename T>
    struct TypeTraits {
        static constexpr bool is_pointer = std::is_pointer_v<T>;
        static constexpr bool is_const = std::is_const_v<T>;
        static constexpr std::size_t size = sizeof(T);
        using type = T;
        using pointer_type = T*;
        using reference_type = T&;
    };

    // Recursive template metaprogramming
    template<std::size_t N>
    struct Factorial {
        static constexpr std::size_t value = N * Factorial<N - 1>::value;
    };

    template<>
    struct Factorial<0> {
        static constexpr std::size_t value = 1;
    };

    template<>
    struct Factorial<1> {
        static constexpr std::size_t value = 1;
    };

    // CRTP (Curiously Recurring Template Pattern)
    template<typename Derived>
    class CRTPBase {
    public:
        void interface() {
            static_cast<Derived*>(this)->implementation();
        }

        void commonFunction() {
            // Common functionality
        }

    protected:
        CRTPBase() = default;
        ~CRTPBase() = default;
    };

    class CRTPDerived : public CRTPBase<CRTPDerived> {
    public:
        void implementation() {
            // Specific implementation
        }
    };
}
`;

      const result = await parserManager.parseFile('templates.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'templates.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const variadicTuple = symbols.find(s => s.name === 'VariadicTuple');
      expect(variadicTuple).toBeDefined();
      expect(variadicTuple?.signature).toContain('typename... Args');

      const variadicConstructor = symbols.filter(s => s.kind === SymbolKind.Constructor && s.signature?.includes('UArgs&&... args'));
      expect(variadicConstructor.length).toBeGreaterThanOrEqual(1);

      const sumFunction = symbols.find(s => s.name === 'sum');
      expect(sumFunction).toBeDefined();
      expect(sumFunction?.signature).toContain('template');

      const sfinaeExamples = symbols.find(s => s.name === 'SFINAEExamples');
      expect(sfinaeExamples).toBeDefined();

      const processIntegral = symbols.find(s => s.name === 'processIntegral');
      expect(processIntegral).toBeDefined();
      expect(processIntegral?.signature).toContain('std::enable_if_t');

      const processFloating = symbols.find(s => s.name === 'processFloating');
      expect(processFloating).toBeDefined();
      expect(processFloating?.signature).toContain('std::is_floating_point_v');

      const hasBegin = symbols.find(s => s.name === 'hasBegin');
      expect(hasBegin).toBeDefined();
      expect(hasBegin?.signature).toContain('decltype');

      const processType = symbols.find(s => s.name === 'processType');
      expect(processType).toBeDefined();
      expect(processType?.signature).toContain('template<typename U>');

      const arrayTemplate = symbols.find(s => s.name === 'Array');
      expect(arrayTemplate).toBeDefined();
      expect(arrayTemplate?.signature).toContain('template<typename T, std::size_t N>');

      const templateTemplateExample = symbols.find(s => s.name === 'TemplateTemplateExample');
      expect(templateTemplateExample).toBeDefined();
      expect(templateTemplateExample?.signature).toContain('template<template<typename> class Container');

      const typeTraits = symbols.find(s => s.name === 'TypeTraits');
      expect(typeTraits).toBeDefined();
      expect(typeTraits?.kind).toBe(SymbolKind.Struct);

      const factorial = symbols.find(s => s.name === 'Factorial');
      expect(factorial).toBeDefined();
      expect(factorial?.signature).toContain('template<std::size_t N>');

      const crtpBase = symbols.find(s => s.name === 'CRTPBase');
      expect(crtpBase).toBeDefined();
      expect(crtpBase?.signature).toContain('template<typename Derived>');

      const crtpDerived = symbols.find(s => s.name === 'CRTPDerived');
      expect(crtpDerived).toBeDefined();
      expect(crtpDerived?.signature).toContain(': public CRTPBase<CRTPDerived>');

      const implementation = symbols.find(s => s.name === 'implementation');
      expect(implementation).toBeDefined();
      expect(implementation?.kind).toBe(SymbolKind.Method);
    });
  });

  describe('Concurrency and Threading', () => {
    it('should extract threading, async, and synchronization primitives', async () => {
      const cppCode = `
#include <thread>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include <future>
#include <semaphore>

namespace Concurrency {
    class ThreadSafeCounter {
    private:
        mutable std::mutex mutex_;
        std::atomic<int> counter_{0};
        std::condition_variable cv_;

    public:
        void increment() {
            std::lock_guard<std::mutex> lock(mutex_);
            ++counter_;
            cv_.notify_one();
        }

        void waitForValue(int target) {
            std::unique_lock<std::mutex> lock(mutex_);
            cv_.wait(lock, [this, target] { return counter_.load() >= target; });
        }

        int getValue() const {
            return counter_.load(std::memory_order_acquire);
        }

        void setValue(int value) {
            counter_.store(value, std::memory_order_release);
        }
    };

    class ThreadPool {
    private:
        std::vector<std::thread> workers_;
        std::queue<std::function<void()>> tasks_;
        std::mutex queue_mutex_;
        std::condition_variable condition_;
        std::atomic<bool> stop_{false};

    public:
        explicit ThreadPool(size_t num_threads) {
            for (size_t i = 0; i < num_threads; ++i) {
                workers_.emplace_back([this] {
                    while (true) {
                        std::function<void()> task;

                        {
                            std::unique_lock<std::mutex> lock(queue_mutex_);
                            condition_.wait(lock, [this] {
                                return stop_.load() || !tasks_.empty();
                            });

                            if (stop_.load() && tasks_.empty()) {
                                return;
                            }

                            task = std::move(tasks_.front());
                            tasks_.pop();
                        }

                        task();
                    }
                });
            }
        }

        template<typename F, typename... Args>
        auto enqueue(F&& f, Args&&... args)
            -> std::future<typename std::result_of_t<F(Args...)>> {
            using return_type = typename std::result_of_t<F(Args...)>;

            auto task = std::make_shared<std::packaged_task<return_type()>>(
                std::bind(std::forward<F>(f), std::forward<Args>(args)...)
            );

            std::future<return_type> result = task->get_future();

            {
                std::unique_lock<std::mutex> lock(queue_mutex_);
                if (stop_.load()) {
                    throw std::runtime_error("enqueue on stopped ThreadPool");
                }
                tasks_.emplace([task] { (*task)(); });
            }

            condition_.notify_one();
            return result;
        }

        ~ThreadPool() {
            {
                std::unique_lock<std::mutex> lock(queue_mutex_);
                stop_.store(true);
            }

            condition_.notify_all();

            for (std::thread& worker : workers_) {
                worker.join();
            }
        }
    };

    // Async operations
    class AsyncOperations {
    public:
        std::future<int> computeAsync(int value) {
            return std::async(std::launch::async, [value] {
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                return value * value;
            });
        }

        std::future<std::string> processDataAsync(const std::string& data) {
            return std::async(std::launch::deferred, [data] {
                return "Processed: " + data;
            });
        }

        void demonstratePromise() {
            std::promise<int> promise;
            std::future<int> future = promise.get_future();

            std::thread worker([&promise] {
                std::this_thread::sleep_for(std::chrono::seconds(1));
                promise.set_value(42);
            });

            int result = future.get();
            worker.join();
        }
    };

    // C++20 Coroutines (if supported)
#ifdef __cpp_coroutines
    #include <coroutine>

    struct Task {
        struct promise_type {
            Task get_return_object() {
                return Task{std::coroutine_handle<promise_type>::from_promise(*this)};
            }

            std::suspend_never initial_suspend() { return {}; }
            std::suspend_never final_suspend() noexcept { return {}; }
            void return_void() {}
            void unhandled_exception() {}
        };

        std::coroutine_handle<promise_type> coro;

        Task(std::coroutine_handle<promise_type> h) : coro(h) {}
        ~Task() { if (coro) coro.destroy(); }

        Task(const Task&) = delete;
        Task& operator=(const Task&) = delete;
        Task(Task&& other) : coro(std::exchange(other.coro, {})) {}
        Task& operator=(Task&& other) {
            if (this != &other) {
                if (coro) coro.destroy();
                coro = std::exchange(other.coro, {});
            }
            return *this;
        }
    };

    class CoroutineExamples {
    public:
        Task simpleCoroutine() {
            co_await std::suspend_always{};
            // Do some work
            co_return;
        }

        Task chainedCoroutines() {
            co_await simpleCoroutine();
            co_await std::suspend_always{};
            co_return;
        }
    };
#endif

    // Lock-free data structures
    template<typename T>
    class LockFreeQueue {
    private:
        struct Node {
            std::atomic<T*> data{nullptr};
            std::atomic<Node*> next{nullptr};
        };

        std::atomic<Node*> head_;
        std::atomic<Node*> tail_;

    public:
        LockFreeQueue() {
            Node* dummy = new Node;
            head_.store(dummy);
            tail_.store(dummy);
        }

        void enqueue(T item) {
            Node* new_node = new Node;
            T* data = new T(std::move(item));
            new_node->data.store(data);

            Node* prev_tail = tail_.exchange(new_node);
            prev_tail->next.store(new_node);
        }

        bool dequeue(T& result) {
            Node* head = head_.load();
            Node* next = head->next.load();

            if (next == nullptr) {
                return false;
            }

            T* data = next->data.load();
            if (data == nullptr) {
                return false;
            }

            result = *data;
            delete data;
            head_.store(next);
            delete head;
            return true;
        }

        ~LockFreeQueue() {
            while (Node* old_head = head_.load()) {
                head_.store(old_head->next.load());
                delete old_head;
            }
        }
    };
}
`;

      const result = await parserManager.parseFile('concurrency.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'concurrency.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const threadSafeCounter = symbols.find(s => s.name === 'ThreadSafeCounter');
      expect(threadSafeCounter).toBeDefined();
      expect(threadSafeCounter?.kind).toBe(SymbolKind.Class);

      const increment = symbols.find(s => s.name === 'increment');
      expect(increment).toBeDefined();
      expect(increment?.kind).toBe(SymbolKind.Method);

      const waitForValue = symbols.find(s => s.name === 'waitForValue');
      expect(waitForValue).toBeDefined();
      expect(waitForValue?.signature).toContain('int target');

      const getValue = symbols.find(s => s.name === 'getValue');
      expect(getValue).toBeDefined();
      expect(getValue?.signature).toContain('const');

      const threadPool = symbols.find(s => s.name === 'ThreadPool');
      expect(threadPool).toBeDefined();
      expect(threadPool?.kind).toBe(SymbolKind.Class);

      const enqueue = symbols.find(s => s.name === 'enqueue');
      expect(enqueue).toBeDefined();
      expect(enqueue?.signature).toContain('template<typename F, typename... Args>');
      expect(enqueue?.signature).toContain('std::future');

      const asyncOperations = symbols.find(s => s.name === 'AsyncOperations');
      expect(asyncOperations).toBeDefined();

      const computeAsync = symbols.find(s => s.name === 'computeAsync');
      expect(computeAsync).toBeDefined();
      expect(computeAsync?.signature).toContain('std::future<int>');

      const processDataAsync = symbols.find(s => s.name === 'processDataAsync');
      expect(processDataAsync).toBeDefined();
      expect(processDataAsync?.signature).toContain('std::future<std::string>');

      const demonstratePromise = symbols.find(s => s.name === 'demonstratePromise');
      expect(demonstratePromise).toBeDefined();

      const lockFreeQueue = symbols.find(s => s.name === 'LockFreeQueue');
      expect(lockFreeQueue).toBeDefined();
      expect(lockFreeQueue?.signature).toContain('template<typename T>');

      const lockFreeEnqueue = symbols.find(s => s.name === 'enqueue' && s.signature?.includes('T item'));
      expect(lockFreeEnqueue).toBeDefined();

      const dequeue = symbols.find(s => s.name === 'dequeue');
      expect(dequeue).toBeDefined();
      expect(dequeue?.signature).toContain('bool');

      // Check for atomic fields
      const atomicFields = symbols.filter(s => s.signature?.includes('std::atomic'));
      expect(atomicFields.length).toBeGreaterThan(0);

      // Check for coroutine symbols if supported
      const task = symbols.find(s => s.name === 'Task');
      if (task) {
        expect(task?.kind).toBe(SymbolKind.Struct);
      }
    });
  });

  describe('Exception Handling and RAII', () => {
    it('should extract exception handling and RAII patterns', async () => {
      const cppCode = `
#include <stdexcept>
#include <memory>
#include <fstream>

namespace ExceptionHandling {
    // Custom exception hierarchy
    class BaseException : public std::exception {
    protected:
        std::string message_;

    public:
        explicit BaseException(const std::string& message) : message_(message) {}
        virtual ~BaseException() noexcept = default;

        const char* what() const noexcept override {
            return message_.c_str();
        }
    };

    class NetworkException : public BaseException {
    private:
        int error_code_;

    public:
        NetworkException(const std::string& message, int code)
            : BaseException(message), error_code_(code) {}

        int getErrorCode() const noexcept { return error_code_; }
    };

    class FileException : public BaseException {
    private:
        std::string filename_;

    public:
        FileException(const std::string& message, const std::string& filename)
            : BaseException(message), filename_(filename) {}

        const std::string& getFilename() const noexcept { return filename_; }
    };

    // RAII Resource management
    class FileHandle {
    private:
        FILE* file_;
        std::string filename_;

    public:
        explicit FileHandle(const std::string& filename, const std::string& mode)
            : filename_(filename) {
            file_ = fopen(filename.c_str(), mode.c_str());
            if (!file_) {
                throw FileException("Cannot open file", filename);
            }
        }

        ~FileHandle() {
            if (file_) {
                fclose(file_);
            }
        }

        // Non-copyable but movable
        FileHandle(const FileHandle&) = delete;
        FileHandle& operator=(const FileHandle&) = delete;

        FileHandle(FileHandle&& other) noexcept
            : file_(std::exchange(other.file_, nullptr)),
              filename_(std::move(other.filename_)) {}

        FileHandle& operator=(FileHandle&& other) noexcept {
            if (this != &other) {
                if (file_) fclose(file_);
                file_ = std::exchange(other.file_, nullptr);
                filename_ = std::move(other.filename_);
            }
            return *this;
        }

        size_t read(void* buffer, size_t size) {
            if (!file_) {
                throw std::runtime_error("File not open");
            }
            return fread(buffer, 1, size, file_);
        }

        void write(const void* buffer, size_t size) {
            if (!file_) {
                throw std::runtime_error("File not open");
            }
            if (fwrite(buffer, 1, size, file_) != size) {
                throw FileException("Write failed", filename_);
            }
        }
    };

    // Exception-safe operations
    class ExceptionSafeOperations {
    public:
        void processFiles(const std::vector<std::string>& filenames) {
            std::vector<std::unique_ptr<FileHandle>> handles;

            try {
                // Open all files
                for (const auto& filename : filenames) {
                    handles.emplace_back(
                        std::make_unique<FileHandle>(filename, "r")
                    );
                }

                // Process files
                for (auto& handle : handles) {
                    char buffer[1024];
                    size_t bytes_read = handle->read(buffer, sizeof(buffer));
                    // Process buffer...
                }
            }
            catch (const FileException& e) {
                std::cerr << "File error: " << e.what()
                         << " (file: " << e.getFilename() << ")\n";
                throw; // Re-throw
            }
            catch (const NetworkException& e) {
                std::cerr << "Network error: " << e.what()
                         << " (code: " << e.getErrorCode() << ")\n";
                throw;
            }
            catch (const std::exception& e) {
                std::cerr << "General error: " << e.what() << "\n";
                throw;
            }
            catch (...) {
                std::cerr << "Unknown error occurred\n";
                throw;
            }
            // RAII ensures all FileHandle destructors are called
        }

        template<typename Func>
        auto withExceptionHandling(Func func) noexcept
            -> std::optional<decltype(func())> {
            try {
                return func();
            }
            catch (const std::exception& e) {
                std::cerr << "Exception caught: " << e.what() << "\n";
                return std::nullopt;
            }
            catch (...) {
                std::cerr << "Unknown exception caught\n";
                return std::nullopt;
            }
        }

        // Exception specifications
        void noThrowFunction() noexcept {
            // This function promises not to throw
        }

        int mayThrowFunction() {
            if (std::rand() % 2) {
                throw std::runtime_error("Random error");
            }
            return 42;
        }
    };

    // RAII Lock wrapper
    template<typename Mutex>
    class ScopedLock {
    private:
        Mutex& mutex_;
        bool locked_;

    public:
        explicit ScopedLock(Mutex& mutex) : mutex_(mutex), locked_(false) {
            mutex_.lock();
            locked_ = true;
        }

        ~ScopedLock() {
            if (locked_) {
                mutex_.unlock();
            }
        }

        // Non-copyable, non-movable for simplicity
        ScopedLock(const ScopedLock&) = delete;
        ScopedLock& operator=(const ScopedLock&) = delete;
        ScopedLock(ScopedLock&&) = delete;
        ScopedLock& operator=(ScopedLock&&) = delete;

        void unlock() {
            if (locked_) {
                mutex_.unlock();
                locked_ = false;
            }
        }

        void lock() {
            if (!locked_) {
                mutex_.lock();
                locked_ = true;
            }
        }
    };

    // Exception-safe factory
    class ResourceFactory {
    public:
        template<typename T, typename... Args>
        static std::unique_ptr<T> createResource(Args&&... args) {
            try {
                return std::make_unique<T>(std::forward<Args>(args)...);
            }
            catch (...) {
                // Log error, cleanup if needed
                throw; // Re-throw
            }
        }

        static std::shared_ptr<FileHandle> createSharedFile(
            const std::string& filename, const std::string& mode) {
            return std::shared_ptr<FileHandle>(
                new FileHandle(filename, mode),
                [](FileHandle* ptr) {
                    // Custom deleter with logging
                    std::cout << "Closing file\n";
                    delete ptr;
                }
            );
        }
    };
}
`;

      const result = await parserManager.parseFile('exceptions.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'exceptions.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      const baseException = symbols.find(s => s.name === 'BaseException');
      expect(baseException).toBeDefined();
      expect(baseException?.kind).toBe(SymbolKind.Class);
      expect(baseException?.signature).toContain(': public std::exception');

      const networkException = symbols.find(s => s.name === 'NetworkException');
      expect(networkException).toBeDefined();
      expect(networkException?.signature).toContain(': public BaseException');

      const fileException = symbols.find(s => s.name === 'FileException');
      expect(fileException).toBeDefined();
      expect(fileException?.signature).toContain(': public BaseException');

      const what = symbols.find(s => s.name === 'what');
      expect(what).toBeDefined();
      expect(what?.signature).toContain('const noexcept override');

      const getErrorCode = symbols.find(s => s.name === 'getErrorCode');
      expect(getErrorCode).toBeDefined();
      expect(getErrorCode?.signature).toContain('noexcept');

      const fileHandle = symbols.find(s => s.name === 'FileHandle');
      expect(fileHandle).toBeDefined();
      expect(fileHandle?.kind).toBe(SymbolKind.Class);

      const fileHandleDestructor = symbols.find(s => s.name === '~FileHandle');
      expect(fileHandleDestructor).toBeDefined();
      expect(fileHandleDestructor?.kind).toBe(SymbolKind.Destructor);

      const moveConstructor = symbols.filter(s => s.kind === SymbolKind.Constructor && s.signature?.includes('&&'));
      expect(moveConstructor.length).toBeGreaterThanOrEqual(1);

      const moveAssignment = symbols.filter(s => s.signature?.includes('operator=') && s.signature?.includes('&&'));
      expect(moveAssignment.length).toBeGreaterThanOrEqual(1);

      const read = symbols.find(s => s.name === 'read');
      expect(read).toBeDefined();
      expect(read?.signature).toContain('size_t');

      const write = symbols.find(s => s.name === 'write');
      expect(write).toBeDefined();
      expect(write?.signature).toContain('const void* buffer');

      const exceptionSafeOperations = symbols.find(s => s.name === 'ExceptionSafeOperations');
      expect(exceptionSafeOperations).toBeDefined();

      const processFiles = symbols.find(s => s.name === 'processFiles');
      expect(processFiles).toBeDefined();
      expect(processFiles?.signature).toContain('std::vector<std::string>');

      const withExceptionHandling = symbols.find(s => s.name === 'withExceptionHandling');
      expect(withExceptionHandling).toBeDefined();
      expect(withExceptionHandling?.signature).toContain('template<typename Func>');
      expect(withExceptionHandling?.signature).toContain('noexcept');

      const noThrowFunction = symbols.find(s => s.name === 'noThrowFunction');
      expect(noThrowFunction).toBeDefined();
      expect(noThrowFunction?.signature).toContain('noexcept');

      const mayThrowFunction = symbols.find(s => s.name === 'mayThrowFunction');
      expect(mayThrowFunction).toBeDefined();
      expect(mayThrowFunction?.signature).toContain('int');

      const scopedLock = symbols.find(s => s.name === 'ScopedLock');
      expect(scopedLock).toBeDefined();
      expect(scopedLock?.signature).toContain('template<typename Mutex>');

      const scopedLockDestructor = symbols.find(s => s.name === '~ScopedLock');
      expect(scopedLockDestructor).toBeDefined();

      const unlock = symbols.find(s => s.name === 'unlock');
      expect(unlock).toBeDefined();
      expect(unlock?.kind).toBe(SymbolKind.Method);

      const resourceFactory = symbols.find(s => s.name === 'ResourceFactory');
      expect(resourceFactory).toBeDefined();

      const createResource = symbols.find(s => s.name === 'createResource');
      expect(createResource).toBeDefined();
      expect(createResource?.signature).toContain('template<typename T, typename... Args>');
      expect(createResource?.signature).toContain('std::unique_ptr<T>');

      const createSharedFile = symbols.find(s => s.name === 'createSharedFile');
      expect(createSharedFile).toBeDefined();
      expect(createSharedFile?.signature).toContain('std::shared_ptr<FileHandle>');
    });
  });

  describe('C++ Testing Patterns', () => {
    it('should extract Google Test, Catch2, and Boost.Test patterns', async () => {
      const cppCode = `
#include <gtest/gtest.h>
#include <gmock/gmock.h>
#include <catch2/catch.hpp>
#include <boost/test/unit_test.hpp>

namespace TestingPatterns {
    // Google Test patterns
    class CalculatorTest : public ::testing::Test {
    protected:
        void SetUp() override {
            calculator = std::make_unique<Calculator>();
        }

        void TearDown() override {
            calculator.reset();
        }

        std::unique_ptr<Calculator> calculator;
    };

    TEST_F(CalculatorTest, AdditionTest) {
        EXPECT_EQ(calculator->add(2, 3), 5);
        ASSERT_NE(calculator.get(), nullptr);
    }

    TEST(MathTest, BasicOperations) {
        EXPECT_EQ(2 + 2, 4);
        EXPECT_DOUBLE_EQ(3.14, 3.14);
        EXPECT_TRUE(true);
        EXPECT_FALSE(false);
    }

    // Parameterized tests
    class ParameterizedTest : public ::testing::TestWithParam<std::pair<int, int>> {
    };

    TEST_P(ParameterizedTest, Addition) {
        auto [a, b] = GetParam();
        Calculator calc;
        EXPECT_GT(calc.add(a, b), a);
    }

    INSTANTIATE_TEST_SUITE_P(
        AdditionTests,
        ParameterizedTest,
        ::testing::Values(
            std::make_pair(1, 2),
            std::make_pair(3, 4),
            std::make_pair(5, 6)
        )
    );

    // Google Mock patterns
    class MockDatabase {
    public:
        MOCK_METHOD(bool, connect, (const std::string& host), ());
        MOCK_METHOD(std::vector<User>, getUsers, (), (const));
        MOCK_METHOD(void, saveUser, (const User& user), ());
        MOCK_METHOD(bool, deleteUser, (int id), ());
    };

    class ServiceTest : public ::testing::Test {
    protected:
        ::testing::StrictMock<MockDatabase> mock_db;
        std::unique_ptr<UserService> service;

        void SetUp() override {
            service = std::make_unique<UserService>(&mock_db);
        }
    };

    TEST_F(ServiceTest, GetUsersCallsDatabase) {
        std::vector<User> expected_users = {{1, "John"}, {2, "Jane"}};

        EXPECT_CALL(mock_db, connect(::testing::_))
            .WillOnce(::testing::Return(true));

        EXPECT_CALL(mock_db, getUsers())
            .WillOnce(::testing::Return(expected_users));

        auto users = service->getAllUsers();
        ASSERT_EQ(users.size(), 2);
    }

    // Catch2 patterns
    TEST_CASE("Vector operations", "[vector]") {
        std::vector<int> v{1, 2, 3};

        SECTION("Adding elements") {
            v.push_back(4);
            REQUIRE(v.size() == 4);
            CHECK(v[3] == 4);
        }

        SECTION("Removing elements") {
            v.pop_back();
            REQUIRE(v.size() == 2);
            CHECK(v.back() == 2);
        }
    }

    SCENARIO("Calculator basic operations", "[calculator]") {
        GIVEN("A calculator") {
            Calculator calc;

            WHEN("I add two numbers") {
                int result = calc.add(2, 3);

                THEN("The result should be correct") {
                    REQUIRE(result == 5);
                }
            }

            WHEN("I divide by zero") {
                THEN("An exception should be thrown") {
                    REQUIRE_THROWS_AS(calc.divide(5, 0), std::runtime_error);
                }
            }
        }
    }

    // Template test
    TEMPLATE_TEST_CASE("Generic container tests", "[template]",
                       std::vector<int>, std::list<int>, std::deque<int>) {
        TestType container;
        container.push_back(1);
        container.push_back(2);

        REQUIRE(container.size() == 2);
        CHECK(container.front() == 1);
    }

    // Boost.Test patterns
    BOOST_AUTO_TEST_SUITE(MathTestSuite)

    BOOST_AUTO_TEST_CASE(addition_test) {
        Calculator calc;
        BOOST_CHECK_EQUAL(calc.add(2, 3), 5);
        BOOST_REQUIRE_GT(calc.add(1, 1), 0);
    }

    BOOST_AUTO_TEST_CASE(division_test) {
        Calculator calc;
        BOOST_CHECK_THROW(calc.divide(5, 0), std::runtime_error);
        BOOST_CHECK_NO_THROW(calc.divide(6, 2));
    }

    BOOST_AUTO_TEST_SUITE_END()

    // Fixtures
    struct DatabaseFixture {
        DatabaseFixture() {
            db = std::make_unique<TestDatabase>();
            db->connect("test_db");
        }

        ~DatabaseFixture() {
            db->disconnect();
        }

        std::unique_ptr<TestDatabase> db;
    };

    BOOST_FIXTURE_TEST_SUITE(DatabaseTests, DatabaseFixture)

    BOOST_AUTO_TEST_CASE(connection_test) {
        BOOST_CHECK(db->isConnected());
    }

    BOOST_AUTO_TEST_CASE(query_test) {
        auto result = db->query("SELECT * FROM users");
        BOOST_CHECK(!result.empty());
    }

    BOOST_AUTO_TEST_SUITE_END()

    // Test utilities
    class TestUtilities {
    public:
        template<typename T>
        static void assertContainerEqual(const T& expected, const T& actual) {
            ASSERT_EQ(expected.size(), actual.size());
            auto it1 = expected.begin();
            auto it2 = actual.begin();
            while (it1 != expected.end()) {
                EXPECT_EQ(*it1, *it2);
                ++it1;
                ++it2;
            }
        }

        static void assertDoubleEqual(double expected, double actual, double tolerance = 1e-9) {
            EXPECT_NEAR(expected, actual, tolerance);
        }

        template<typename Exception, typename Func>
        static void assertThrows(Func func) {
            bool threw = false;
            try {
                func();
            } catch (const Exception&) {
                threw = true;
            }
            EXPECT_TRUE(threw);
        }
    };

    // Supporting classes for tests
    class Calculator {
    public:
        int add(int a, int b) { return a + b; }
        int subtract(int a, int b) { return a - b; }
        double divide(double a, double b) {
            if (b == 0) throw std::runtime_error("Division by zero");
            return a / b;
        }
    };

    struct User {
        int id;
        std::string name;
    };

    class UserService {
    private:
        MockDatabase* db_;
    public:
        explicit UserService(MockDatabase* db) : db_(db) {}

        std::vector<User> getAllUsers() {
            if (db_->connect("localhost")) {
                return db_->getUsers();
            }
            return {};
        }
    };

    class TestDatabase {
    public:
        bool connect(const std::string& name) { return true; }
        void disconnect() {}
        bool isConnected() { return true; }
        std::vector<std::string> query(const std::string& sql) { return {"row1", "row2"}; }
    };
}
`;

      const result = await parserManager.parseFile('testing.cpp', cppCode);
      const extractor = new CppExtractor('cpp', 'testing.cpp', cppCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Google Test patterns
      const calculatorTest = symbols.find(s => s.name === 'CalculatorTest');
      expect(calculatorTest).toBeDefined();
      expect(calculatorTest?.signature).toContain(': public ::testing::Test');

      const setUp = symbols.find(s => s.name === 'SetUp');
      expect(setUp).toBeDefined();
      expect(setUp?.signature).toContain('override');

      const tearDown = symbols.find(s => s.name === 'TearDown');
      expect(tearDown).toBeDefined();
      expect(tearDown?.signature).toContain('override');

      // Test macros would be handled by the parser as function-like constructs
      const parameterizedTest = symbols.find(s => s.name === 'ParameterizedTest');
      expect(parameterizedTest).toBeDefined();
      expect(parameterizedTest?.signature).toContain('::testing::TestWithParam');

      // Google Mock patterns
      const mockDatabase = symbols.find(s => s.name === 'MockDatabase');
      expect(mockDatabase).toBeDefined();
      expect(mockDatabase?.kind).toBe(SymbolKind.Class);

      const serviceTest = symbols.find(s => s.name === 'ServiceTest');
      expect(serviceTest).toBeDefined();
      expect(serviceTest?.signature).toContain(': public ::testing::Test');

      // Supporting classes
      const calculator = symbols.find(s => s.name === 'Calculator');
      expect(calculator).toBeDefined();
      expect(calculator?.kind).toBe(SymbolKind.Class);

      const add = symbols.find(s => s.name === 'add');
      expect(add).toBeDefined();
      expect(add?.signature).toContain('int add(int a, int b)');

      const subtract = symbols.find(s => s.name === 'subtract');
      expect(subtract).toBeDefined();
      expect(subtract?.signature).toContain('int subtract(int a, int b)');

      const divide = symbols.find(s => s.name === 'divide');
      expect(divide).toBeDefined();
      expect(divide?.signature).toContain('double divide(double a, double b)');

      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.kind).toBe(SymbolKind.Struct);

      const userService = symbols.find(s => s.name === 'UserService');
      expect(userService).toBeDefined();
      expect(userService?.kind).toBe(SymbolKind.Class);

      const getAllUsers = symbols.find(s => s.name === 'getAllUsers');
      expect(getAllUsers).toBeDefined();
      expect(getAllUsers?.signature).toContain('std::vector<User>');

      const testDatabase = symbols.find(s => s.name === 'TestDatabase');
      expect(testDatabase).toBeDefined();
      expect(testDatabase?.kind).toBe(SymbolKind.Class);

      const connect = symbols.find(s => s.name === 'connect');
      expect(connect).toBeDefined();
      expect(connect?.signature).toContain('const std::string& name');

      const query = symbols.find(s => s.name === 'query');
      expect(query).toBeDefined();
      expect(query?.signature).toContain('std::vector<std::string>');

      // Test utilities
      const testUtilities = symbols.find(s => s.name === 'TestUtilities');
      expect(testUtilities).toBeDefined();
      expect(testUtilities?.kind).toBe(SymbolKind.Class);

      const assertContainerEqual = symbols.find(s => s.name === 'assertContainerEqual');
      expect(assertContainerEqual).toBeDefined();
      expect(assertContainerEqual?.signature).toContain('template<typename T>');
      expect(assertContainerEqual?.signature).toContain('static');

      const assertDoubleEqual = symbols.find(s => s.name === 'assertDoubleEqual');
      expect(assertDoubleEqual).toBeDefined();
      expect(assertDoubleEqual?.signature).toContain('static');
      expect(assertDoubleEqual?.signature).toContain('tolerance = 1e-9');

      const assertThrows = symbols.find(s => s.name === 'assertThrows');
      expect(assertThrows).toBeDefined();
      expect(assertThrows?.signature).toContain('template<typename Exception, typename Func>');

      // Fixture pattern
      const databaseFixture = symbols.find(s => s.name === 'DatabaseFixture');
      expect(databaseFixture).toBeDefined();
      expect(databaseFixture?.kind).toBe(SymbolKind.Struct);

      const fixtureConstructor = symbols.filter(s => s.kind === SymbolKind.Constructor && s.signature?.includes('DatabaseFixture'));
      expect(fixtureConstructor.length).toBeGreaterThanOrEqual(1);

      const fixtureDestructor = symbols.find(s => s.name === '~DatabaseFixture');
      expect(fixtureDestructor).toBeDefined();
      expect(fixtureDestructor?.kind).toBe(SymbolKind.Destructor);
    });
  });

  describe('Performance Testing', () => {
    it('should handle large C++ codebases with complex templates efficiently', async () => {
      const generateTemplateClass = (index: number) => `
    template<typename T${index}, typename U${index} = std::string>
    class TemplateClass${index} : public BaseTemplate<T${index}> {
    private:
        std::shared_ptr<T${index}> data_;
        std::vector<U${index}> items_;
        std::unordered_map<std::string, T${index}> cache_;
        mutable std::mutex mutex_;
        std::atomic<size_t> access_count_{0};

    public:
        explicit TemplateClass${index}(std::shared_ptr<T${index}> data)
            : data_(std::move(data)) {}

        template<typename... Args>
        TemplateClass${index}(Args&&... args)
            : data_(std::make_shared<T${index}>(std::forward<Args>(args)...)) {}

        TemplateClass${index}(const TemplateClass${index}& other)
            : data_(other.data_), items_(other.items_) {}

        TemplateClass${index}(TemplateClass${index}&& other) noexcept
            : data_(std::move(other.data_)), items_(std::move(other.items_)) {}

        TemplateClass${index}& operator=(const TemplateClass${index}& other) {
            if (this != &other) {
                std::lock_guard<std::mutex> lock(mutex_);
                data_ = other.data_;
                items_ = other.items_;
            }
            return *this;
        }

        TemplateClass${index}& operator=(TemplateClass${index}&& other) noexcept {
            if (this != &other) {
                std::lock_guard<std::mutex> lock(mutex_);
                data_ = std::move(other.data_);
                items_ = std::move(other.items_);
            }
            return *this;
        }

        virtual ~TemplateClass${index}() = default;

        template<typename Func>
        auto process(Func func) -> decltype(func(*data_)) {
            std::lock_guard<std::mutex> lock(mutex_);
            ++access_count_;
            return func(*data_);
        }

        template<typename V>
        void addItem(V&& item) {
            std::lock_guard<std::mutex> lock(mutex_);
            items_.emplace_back(std::forward<V>(item));
        }

        const T${index}& getData() const {
            ++access_count_;
            return *data_;
        }

        void setData(const T${index}& new_data) {
            std::lock_guard<std::mutex> lock(mutex_);
            *data_ = new_data;
        }

        size_t getAccessCount() const noexcept {
            return access_count_.load(std::memory_order_relaxed);
        }

        template<typename Key>
        void cacheItem(Key&& key, const T${index}& item) {
            std::lock_guard<std::mutex> lock(mutex_);
            cache_[std::forward<Key>(key)] = item;
        }

        template<typename Key>
        std::optional<T${index}> getCachedItem(const Key& key) const {
            std::lock_guard<std::mutex> lock(mutex_);
            auto it = cache_.find(key);
            return (it != cache_.end()) ? std::make_optional(it->second) : std::nullopt;
        }

        // Operator overloads
        T${index}& operator*() { return *data_; }
        const T${index}& operator*() const { return *data_; }
        T${index}* operator->() { return data_.get(); }
        const T${index}* operator->() const { return data_.get(); }

        // Comparison operators
        bool operator==(const TemplateClass${index}& other) const {
            return data_ == other.data_;
        }

        bool operator!=(const TemplateClass${index}& other) const {
            return !(*this == other);
        }

        // Stream operators
        template<typename StreamT, typename DataT, typename ItemT>
        friend std::ostream& operator<<(std::ostream& os, const TemplateClass${index}<DataT, ItemT>& obj);
    };

    // Specialized algorithms for this template
    template<typename T${index}, typename U${index}>
    std::future<void> processAsync${index}(TemplateClass${index}<T${index}, U${index}>& obj) {
        return std::async(std::launch::async, [&obj]() {
            obj.process([](const T${index}& data) {
                // Simulate processing
                std::this_thread::sleep_for(std::chrono::microseconds(10));
                return data;
            });
        });
    }

    template<typename T${index}>
    class Factory${index} {
    public:
        template<typename... Args>
        static std::unique_ptr<TemplateClass${index}<T${index}>> create(Args&&... args) {
            return std::make_unique<TemplateClass${index}<T${index}>>(std::forward<Args>(args)...);
        }

        static std::shared_ptr<TemplateClass${index}<T${index}>> createShared(const T${index}& data) {
            return std::make_shared<TemplateClass${index}<T${index}>>(std::make_shared<T${index}>(data));
        }
    };

    // Template specializations
    template<>
    class TemplateClass${index}<int> {
    private:
        int value_;
    public:
        explicit TemplateClass${index}(int value) : value_(value) {}
        int getValue() const { return value_; }
        void setValue(int value) { value_ = value; }
    };

    template<>
    class TemplateClass${index}<std::string> {
    private:
        std::string value_;
    public:
        explicit TemplateClass${index}(const std::string& value) : value_(value) {}
        const std::string& getValue() const { return value_; }
        void setValue(const std::string& value) { value_ = value; }
        size_t length() const { return value_.length(); }
    }`;

      const largeCodebase = `
#include <memory>
#include <vector>
#include <unordered_map>
#include <string>
#include <mutex>
#include <atomic>
#include <future>
#include <thread>
#include <optional>
#include <iostream>
#include <algorithm>
#include <chrono>

namespace PerformanceTest {
    // Base template class
    template<typename T>
    class BaseTemplate {
    protected:
        virtual ~BaseTemplate() = default;
    public:
        virtual void baseMethod() = 0;
    };

    // Utility traits
    template<typename T>
    struct TypeTraits {
        static constexpr bool is_arithmetic = std::is_arithmetic_v<T>;
        static constexpr bool is_pointer = std::is_pointer_v<T>;
        static constexpr size_t size = sizeof(T);
        using value_type = T;
        using pointer_type = T*;
        using reference_type = T&;
    };

    // SFINAE helpers
    template<typename T, typename = void>
    struct has_size : std::false_type {};

    template<typename T>
    struct has_size<T, std::void_t<decltype(std::declval<T>().size())>> : std::true_type {};

    template<typename T>
    constexpr bool has_size_v = has_size<T>::value;
${Array.from({ length: 15 }, (_, i) => generateTemplateClass(i + 1)).join('')}

    // Complex inheritance hierarchy
    template<typename T>
    class ComplexHierarchy : public TemplateClass1<T>, public TemplateClass2<T> {
    public:
        using TemplateClass1<T>::process;
        using TemplateClass2<T>::addItem;

        template<typename U>
        void complexOperation(U&& value) {
            this->TemplateClass1<T>::process([&value](const T& data) {
                return data;
            });
            this->TemplateClass2<T>::addItem(std::forward<U>(value));
        }
    };

    // Variadic template function
    template<typename... Types>
    auto processMultiple(Types&&... args) {
        return std::make_tuple(processAsync1(args)...);
    }

    // Template metaprogramming
    template<size_t N>
    struct Fibonacci {
        static constexpr size_t value = Fibonacci<N-1>::value + Fibonacci<N-2>::value;
    };

    template<> struct Fibonacci<0> { static constexpr size_t value = 0; };
    template<> struct Fibonacci<1> { static constexpr size_t value = 1; };

    // Type list manipulation
    template<typename... Types>
    struct TypeList {};

    template<typename List, typename T>
    struct Append;

    template<typename... Types, typename T>
    struct Append<TypeList<Types...>, T> {
        using type = TypeList<Types..., T>;
    };

    // Complex template alias
    template<typename T>
    using ComplexAlias = std::unordered_map<std::string, std::shared_ptr<TemplateClass1<std::vector<T>>>>;
}
`;

      const startTime = performance.now();
      const result = await parserManager.parseFile('performance-test.cpp', largeCodebase);
      const extractor = new CppExtractor('cpp', 'performance-test.cpp', largeCodebase);
      const symbols = extractor.extractSymbols(result.tree);
      const endTime = performance.now();

      // Should extract all symbols efficiently
      expect(symbols.length).toBeGreaterThan(300); // Many symbols expected
      expect(endTime - startTime).toBeLessThan(10000); // Should complete within 10 seconds

      // Verify template classes are extracted
      const templateClasses = symbols.filter(s => s.name.startsWith('TemplateClass') && s.kind === SymbolKind.Class);
      expect(templateClasses.length).toBeGreaterThanOrEqual(15);

      // Verify template methods
      const processMethods = symbols.filter(s => s.name === 'process');
      expect(processMethods.length).toBeGreaterThanOrEqual(15);

      // Verify constructors and destructors
      const constructors = symbols.filter(s => s.kind === SymbolKind.Constructor);
      expect(constructors.length).toBeGreaterThanOrEqual(30); // Multiple constructors per class

      const destructors = symbols.filter(s => s.kind === SymbolKind.Destructor);
      expect(destructors.length).toBeGreaterThanOrEqual(15);

      // Verify operators
      const operators = symbols.filter(s => s.name.includes('operator'));
      expect(operators.length).toBeGreaterThanOrEqual(45); // Multiple operators per class

      // Verify factory methods
      const factories = symbols.filter(s => s.name.startsWith('Factory'));
      expect(factories.length).toBe(15);

      // Verify async functions
      const asyncFunctions = symbols.filter(s => s.name.includes('processAsync'));
      expect(asyncFunctions.length).toBe(15);

      // Verify template specializations
      const intSpecializations = symbols.filter(s => s.signature?.includes('<int>'));
      expect(intSpecializations.length).toBeGreaterThanOrEqual(15);

      const stringSpecializations = symbols.filter(s => s.signature?.includes('<std::string>'));
      expect(stringSpecializations.length).toBeGreaterThanOrEqual(15);

      // Verify complex template structures
      const complexHierarchy = symbols.find(s => s.name === 'ComplexHierarchy');
      expect(complexHierarchy).toBeDefined();
      expect(complexHierarchy?.signature).toContain('TemplateClass1<T>, public TemplateClass2<T>');

      const processMultiple = symbols.find(s => s.name === 'processMultiple');
      expect(processMultiple).toBeDefined();
      expect(processMultiple?.signature).toContain('template<typename... Types>');

      const fibonacci = symbols.find(s => s.name === 'Fibonacci');
      expect(fibonacci).toBeDefined();
      expect(fibonacci?.signature).toContain('template<size_t N>');

      const typeList = symbols.find(s => s.name === 'TypeList');
      expect(typeList).toBeDefined();
      expect(typeList?.signature).toContain('template<typename... Types>');
    });
  });

  describe('Edge Cases and Error Handling', () => {
    it('should handle malformed code, complex nesting, and preprocessor directives', async () => {
      const complexCode = `
#ifdef DEBUG
#define DBG_PRINT(x) std::cout << #x << ": " << (x) << std::endl
#else
#define DBG_PRINT(x)
#endif

#ifndef CUSTOM_ALLOCATOR
#define CUSTOM_ALLOCATOR std::allocator
#endif

// Complex macro with variadic arguments
#define DECLARE_GETTER_SETTER(type, name) \
    private: type name##_; \
    public: \
        const type& get##name() const { return name##_; } \
        void set##name(const type& value) { name##_ = value; }

namespace EdgeCases {
    // Extremely nested templates
    template<
        typename T1,
        typename T2 = std::conditional_t<
            std::is_arithmetic_v<T1>,
            double,
            std::string
        >,
        template<typename> class Container = std::vector,
        typename Allocator = CUSTOM_ALLOCATOR<T1>,
        size_t BufferSize = sizeof(T1) * 64,
        bool EnableOptimization = std::is_trivially_copyable_v<T1>
    >
    class DeepTemplate {
    private:
        using AllocatorType = typename std::allocator_traits<Allocator>::template rebind_alloc<T1>;
        using ContainerType = Container<T1>;

        template<typename U = T1>
        using EnableIfArithmetic = std::enable_if_t<std::is_arithmetic_v<U>, U>;

        template<typename U = T1>
        using EnableIfNotArithmetic = std::enable_if_t<!std::is_arithmetic_v<U>, U>;

    public:
        template<typename U>
        auto processArithmetic(U&& value) -> EnableIfArithmetic<std::decay_t<U>> {
            DBG_PRINT(value);
            return static_cast<std::decay_t<U>>(value * 2);
        }

        template<typename U>
        auto processNonArithmetic(U&& value) -> EnableIfNotArithmetic<std::decay_t<U>> {
            DBG_PRINT("Processing non-arithmetic type");
            return std::forward<U>(value);
        }

        // Deeply nested lambda with capture
        auto createComplexLambda() {
            auto outer_lambda = [this](auto&& outer_arg) {
                return [this, outer_arg](auto&& middle_arg) {
                    return [this, outer_arg, middle_arg](auto&& inner_arg) -> decltype(auto) {
                        if constexpr (std::is_same_v<decltype(inner_arg), int&&>) {
                            return this->processArithmetic(inner_arg + outer_arg + middle_arg);
                        } else {
                            return this->processNonArithmetic(inner_arg);
                        }
                    };
                };
            };
            return outer_lambda;
        }

        DECLARE_GETTER_SETTER(T1, data)
        DECLARE_GETTER_SETTER(T2, metadata)
    };

    // Complex SFINAE with multiple conditions
    template<typename T>
    class SFINAEMadness {
    public:
        // Method enabled only for containers with begin(), end(), and size()
        template<typename U = T>
        auto processContainer(U&& container)
            -> std::enable_if_t<
                std::conjunction_v<
                    std::is_same<std::decay_t<U>, T>,
                    std::negation<std::is_arithmetic<T>>,
                    std::bool_constant<
                        std::is_same_v<
                            decltype(std::declval<T>().begin()),
                            decltype(std::declval<T>().begin())
                        >
                    >
                >,
                decltype(container.size())
            > {
            return container.size();
        }

        // Overload for arithmetic types
        template<typename U = T>
        std::enable_if_t<std::is_arithmetic_v<U>, U> processContainer(U&& value) {
            return value * value;
        }

        // Variadic SFINAE
        template<typename... Args>
        auto variadicProcess(Args&&... args)
            -> std::enable_if_t<
                std::conjunction_v<std::is_convertible<Args, T>...>,
                std::common_type_t<Args...>
            > {
            return (args + ...);
        }
    };

    // Recursive template with specializations
    template<typename T, size_t Depth>
    struct RecursiveStructure {
        using Type = typename RecursiveStructure<T, Depth - 1>::Type*;
        static constexpr size_t depth = Depth;

        template<typename U>
        static auto process(U&& value) -> Type {
            return &RecursiveStructure<T, Depth - 1>::process(std::forward<U>(value));
        }
    };

    template<typename T>
    struct RecursiveStructure<T, 0> {
        using Type = T;
        static constexpr size_t depth = 0;

        template<typename U>
        static T process(U&& value) {
            return static_cast<T>(std::forward<U>(value));
        }
    };

    // Template with dependent name lookup challenges
    template<typename Base>
    class DependentLookup : public Base {
    public:
        using typename Base::value_type;
        using Base::process;

        template<typename T>
        void complexMethod() {
            typename Base::template NestedTemplate<T> nested;
            this->template methodTemplate<T>();
            Base::template staticMethodTemplate<T>();
        }
    };

    // Malformed code sections (commented out to avoid parse errors)
    /*
    // This would normally cause parse errors
    template<typename T
    class IncompleteTemplate {
        void incompleteMethod(
        // Missing closing parenthesis and return type
    */

    // Valid but complex preprocessor usage
#if defined(__cpp_concepts) && __cpp_concepts >= 201907L
    template<typename T>
    concept Processable = requires(T t) {
        { t.process() } -> std::convertible_to<bool>;
        { t.size() } -> std::convertible_to<size_t>;
    };

    template<Processable T>
    class ConceptConstrainedClass {
    public:
        void doWork(T& item) {
            if (item.process()) {
                DBG_PRINT("Processing successful");
            }
        }
    };
#endif

    // Extreme operator overloading
    template<typename T>
    class OperatorMadness {
    private:
        T value_;

    public:
        // Standard operators
        OperatorMadness& operator++() { ++value_; return *this; }
        OperatorMadness operator++(int) { auto temp = *this; ++value_; return temp; }
        OperatorMadness& operator--() { --value_; return *this; }
        OperatorMadness operator--(int) { auto temp = *this; --value_; return temp; }

        // Arithmetic operators
        OperatorMadness operator+(const OperatorMadness& other) const { return {value_ + other.value_}; }
        OperatorMadness operator-(const OperatorMadness& other) const { return {value_ - other.value_}; }
        OperatorMadness operator*(const OperatorMadness& other) const { return {value_ * other.value_}; }
        OperatorMadness operator/(const OperatorMadness& other) const { return {value_ / other.value_}; }
        OperatorMadness operator%(const OperatorMadness& other) const { return {value_ % other.value_}; }

        // Assignment operators
        OperatorMadness& operator+=(const OperatorMadness& other) { value_ += other.value_; return *this; }
        OperatorMadness& operator-=(const OperatorMadness& other) { value_ -= other.value_; return *this; }
        OperatorMadness& operator*=(const OperatorMadness& other) { value_ *= other.value_; return *this; }
        OperatorMadness& operator/=(const OperatorMadness& other) { value_ /= other.value_; return *this; }
        OperatorMadness& operator%=(const OperatorMadness& other) { value_ %= other.value_; return *this; }

        // Bitwise operators
        OperatorMadness operator&(const OperatorMadness& other) const { return {value_ & other.value_}; }
        OperatorMadness operator|(const OperatorMadness& other) const { return {value_ | other.value_}; }
        OperatorMadness operator^(const OperatorMadness& other) const { return {value_ ^ other.value_}; }
        OperatorMadness operator~() const { return {~value_}; }
        OperatorMadness operator<<(int shift) const { return {value_ << shift}; }
        OperatorMadness operator>>(int shift) const { return {value_ >> shift}; }

        // Comparison operators
        bool operator==(const OperatorMadness& other) const { return value_ == other.value_; }
        bool operator!=(const OperatorMadness& other) const { return value_ != other.value_; }
        bool operator<(const OperatorMadness& other) const { return value_ < other.value_; }
        bool operator<=(const OperatorMadness& other) const { return value_ <= other.value_; }
        bool operator>(const OperatorMadness& other) const { return value_ > other.value_; }
        bool operator>=(const OperatorMadness& other) const { return value_ >= other.value_; }

        // Function call operator with multiple overloads
        T operator()() const { return value_; }
        T operator()(T increment) const { return value_ + increment; }
        template<typename U> auto operator()(U&& arg) const -> decltype(value_ + arg) { return value_ + arg; }

        // Array subscript operator
        T& operator[](size_t) { return value_; }
        const T& operator[](size_t) const { return value_; }

        // Pointer-like operators
        T* operator->() { return &value_; }
        const T* operator->() const { return &value_; }
        T& operator*() { return value_; }
        const T& operator*() const { return value_; }

        // Conversion operators
        operator T() const { return value_; }
        explicit operator bool() const { return static_cast<bool>(value_); }

        // Stream operators
        template<typename CharT, typename TraitsT>
        friend std::basic_ostream<CharT, TraitsT>& operator<<(
            std::basic_ostream<CharT, TraitsT>& os, const OperatorMadness& obj) {
            return os << obj.value_;
        }

        template<typename CharT, typename TraitsT>
        friend std::basic_istream<CharT, TraitsT>& operator>>(
            std::basic_istream<CharT, TraitsT>& is, OperatorMadness& obj) {
            return is >> obj.value_;
        }
    };
}
`;

      const result = await parserManager.parseFile('edge-cases.cpp', complexCode);
      const extractor = new CppExtractor('cpp', 'edge-cases.cpp', complexCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should handle complex templates
      const deepTemplate = symbols.find(s => s.name === 'DeepTemplate');
      expect(deepTemplate).toBeDefined();
      expect(deepTemplate?.signature).toContain('template<');
      expect(deepTemplate?.signature).toContain('typename T1');

      const processArithmetic = symbols.find(s => s.name === 'processArithmetic');
      expect(processArithmetic).toBeDefined();
      expect(processArithmetic?.signature).toContain('EnableIfArithmetic');

      const processNonArithmetic = symbols.find(s => s.name === 'processNonArithmetic');
      expect(processNonArithmetic).toBeDefined();
      expect(processNonArithmetic?.signature).toContain('EnableIfNotArithmetic');

      const createComplexLambda = symbols.find(s => s.name === 'createComplexLambda');
      expect(createComplexLambda).toBeDefined();

      // Should handle SFINAE
      const sfinaeMadness = symbols.find(s => s.name === 'SFINAEMadness');
      expect(sfinaeMadness).toBeDefined();

      const processContainer = symbols.find(s => s.name === 'processContainer');
      expect(processContainer).toBeDefined();
      expect(processContainer?.signature).toContain('std::enable_if_t');

      const variadicProcess = symbols.find(s => s.name === 'variadicProcess');
      expect(variadicProcess).toBeDefined();
      expect(variadicProcess?.signature).toContain('typename... Args');

      // Should handle recursive templates
      const recursiveStructure = symbols.find(s => s.name === 'RecursiveStructure');
      expect(recursiveStructure).toBeDefined();
      expect(recursiveStructure?.signature).toContain('template<typename T, size_t Depth>');

      // Should handle dependent lookup
      const dependentLookup = symbols.find(s => s.name === 'DependentLookup');
      expect(dependentLookup).toBeDefined();
      expect(dependentLookup?.signature).toContain(': public Base');

      const complexMethod = symbols.find(s => s.name === 'complexMethod');
      expect(complexMethod).toBeDefined();
      expect(complexMethod?.signature).toContain('template<typename T>');

      // Should handle operator overloading
      const operatorMadness = symbols.find(s => s.name === 'OperatorMadness');
      expect(operatorMadness).toBeDefined();

      const operators = symbols.filter(s => s.name.includes('operator'));
      expect(operators.length).toBeGreaterThan(20); // Many operators

      const incrementOp = symbols.find(s => s.name === 'operator++' && !s.signature?.includes('int'));
      expect(incrementOp).toBeDefined();

      const postIncrementOp = symbols.find(s => s.name === 'operator++' && s.signature?.includes('int'));
      expect(postIncrementOp).toBeDefined();

      const plusOp = symbols.find(s => s.name === 'operator+');
      expect(plusOp).toBeDefined();

      const callOp = symbols.find(s => s.name === 'operator()' && !s.signature?.includes('template'));
      expect(callOp).toBeDefined();

      const templateCallOp = symbols.find(s => s.name === 'operator()' && s.signature?.includes('template'));
      expect(templateCallOp).toBeDefined();

      // Debug: List all operator symbols
      const allOperators = symbols.filter(s => s.name.includes('operator'));
      console.log('[DEBUG] All operator symbols found:');
      allOperators.forEach(op => console.log(`  - ${op.name}: ${op.signature || 'no signature'}`));

      const conversionOp = symbols.find(s => s.name === 'operator T');
      expect(conversionOp).toBeDefined();

      const boolConversionOp = symbols.find(s => s.name === 'operator bool');
      expect(boolConversionOp).toBeDefined();
      expect(boolConversionOp?.signature).toContain('explicit');

      // Should handle concepts if supported
      const conceptConstrainedClass = symbols.find(s => s.name === 'ConceptConstrainedClass');
      if (conceptConstrainedClass) {
        expect(conceptConstrainedClass?.signature).toContain('Processable T');
      }

      // Should not crash on complex code
      expect(symbols.length).toBeGreaterThan(50); // Should extract many valid symbols

      // Should handle template aliases and using declarations
      const usingDeclarations = symbols.filter(s => s.signature?.includes('using'));
      expect(usingDeclarations.length).toBeGreaterThan(0);
    });
  });
});