import { describe, it, expect, beforeAll } from 'bun:test';
import { JavaExtractor } from '../../extractors/java-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('JavaExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Package and Import Extraction', () => {
    it('should extract package declarations', async () => {
      const javaCode = `
package com.example.app;

package com.acme.utils;
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const appPackage = symbols.find(s => s.name === 'com.example.app');
      expect(appPackage).toBeDefined();
      expect(appPackage?.kind).toBe(SymbolKind.Namespace);
      expect(appPackage?.signature).toBe('package com.example.app');
      expect(appPackage?.visibility).toBe('public');
    });

    it('should extract import statements', async () => {
      const javaCode = `
package com.example;

import java.util.List;
import java.util.ArrayList;
import java.util.Map;
import static java.lang.Math.PI;
import static java.util.Collections.*;
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const listImport = symbols.find(s => s.name === 'List');
      expect(listImport).toBeDefined();
      expect(listImport?.kind).toBe(SymbolKind.Import);
      expect(listImport?.signature).toBe('import java.util.List');

      const piImport = symbols.find(s => s.name === 'PI');
      expect(piImport).toBeDefined();
      expect(piImport?.signature).toBe('import static java.lang.Math.PI');

      const collectionsImport = symbols.find(s => s.name === 'Collections');
      expect(collectionsImport).toBeDefined();
      expect(collectionsImport?.signature).toBe('import static java.util.Collections.*');
    });
  });

  describe('Class Extraction', () => {
    it('should extract class definitions with modifiers', async () => {
      const javaCode = `
package com.example;

public class User {
    private String name;
    public int age;
}

abstract class Animal {
    abstract void makeSound();
}

final class Constants {
    public static final String VERSION = "1.0";
}

class DefaultClass {
    // package-private class
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const userClass = symbols.find(s => s.name === 'User');
      expect(userClass).toBeDefined();
      expect(userClass?.kind).toBe(SymbolKind.Class);
      expect(userClass?.signature).toContain('public class User');
      expect(userClass?.visibility).toBe('public');

      const animalClass = symbols.find(s => s.name === 'Animal');
      expect(animalClass).toBeDefined();
      expect(animalClass?.signature).toContain('abstract class Animal');
      expect(animalClass?.visibility).toBe('package');

      const constantsClass = symbols.find(s => s.name === 'Constants');
      expect(constantsClass).toBeDefined();
      expect(constantsClass?.signature).toContain('final class Constants');

      const defaultClass = symbols.find(s => s.name === 'DefaultClass');
      expect(defaultClass).toBeDefined();
      expect(defaultClass?.visibility).toBe('package');
    });

    it('should extract class inheritance and implementations', async () => {
      const javaCode = `
package com.example;

public class Dog extends Animal implements Runnable, Serializable {
    public void run() {}
}

public class Cat extends Animal {
    public void meow() {}
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const dogClass = symbols.find(s => s.name === 'Dog');
      expect(dogClass).toBeDefined();
      expect(dogClass?.signature).toContain('extends Animal');
      expect(dogClass?.signature).toContain('implements Runnable, Serializable');

      const catClass = symbols.find(s => s.name === 'Cat');
      expect(catClass).toBeDefined();
      expect(catClass?.signature).toContain('extends Animal');
    });
  });

  describe('Interface Extraction', () => {
    it('should extract interface definitions', async () => {
      const javaCode = `
package com.example;

public interface Drawable {
    void draw();
    default void render() {
        draw();
    }
}

interface Serializable extends Cloneable {
    // marker interface
}

@FunctionalInterface
public interface Consumer<T> {
    void accept(T t);
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const drawable = symbols.find(s => s.name === 'Drawable');
      expect(drawable).toBeDefined();
      expect(drawable?.kind).toBe(SymbolKind.Interface);
      expect(drawable?.signature).toContain('public interface Drawable');
      expect(drawable?.visibility).toBe('public');

      const serializable = symbols.find(s => s.name === 'Serializable');
      expect(serializable).toBeDefined();
      expect(serializable?.signature).toContain('extends Cloneable');

      const consumer = symbols.find(s => s.name === 'Consumer');
      expect(consumer).toBeDefined();
      expect(consumer?.signature).toContain('Consumer<T>');
    });
  });

  describe('Method Extraction', () => {
    it('should extract method definitions with parameters and return types', async () => {
      const javaCode = `
package com.example;

public class Calculator {
    public int add(int a, int b) {
        return a + b;
    }

    private void reset() {
        // private method
    }

    protected static String format(double value) {
        return String.valueOf(value);
    }

    public abstract void process();

    public final boolean validate(String input) {
        return input != null;
    }

    @Override
    public String toString() {
        return "Calculator";
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const addMethod = symbols.find(s => s.name === 'add');
      expect(addMethod).toBeDefined();
      expect(addMethod?.kind).toBe(SymbolKind.Method);
      expect(addMethod?.signature).toContain('public int add(int a, int b)');
      expect(addMethod?.visibility).toBe('public');

      const resetMethod = symbols.find(s => s.name === 'reset');
      expect(resetMethod).toBeDefined();
      expect(resetMethod?.visibility).toBe('private');

      const formatMethod = symbols.find(s => s.name === 'format');
      expect(formatMethod).toBeDefined();
      expect(formatMethod?.signature).toContain('protected static String format');
      expect(formatMethod?.visibility).toBe('protected');

      const processMethod = symbols.find(s => s.name === 'process');
      expect(processMethod).toBeDefined();
      expect(processMethod?.signature).toContain('abstract');

      const validateMethod = symbols.find(s => s.name === 'validate');
      expect(validateMethod).toBeDefined();
      expect(validateMethod?.signature).toContain('final boolean validate');

      const toStringMethod = symbols.find(s => s.name === 'toString');
      expect(toStringMethod).toBeDefined();
      expect(toStringMethod?.signature).toContain('@Override');
    });

    it('should extract constructors', async () => {
      const javaCode = `
package com.example;

public class Person {
    private String name;
    private int age;

    public Person() {
        this("Unknown", 0);
    }

    public Person(String name) {
        this(name, 0);
    }

    public Person(String name, int age) {
        this.name = name;
        this.age = age;
    }

    private Person(boolean dummy) {
        // private constructor
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const constructors = symbols.filter(s => s.kind === SymbolKind.Constructor);
      expect(constructors).toHaveLength(4);

      const defaultConstructor = constructors.find(s => s.signature?.includes('Person()'));
      expect(defaultConstructor).toBeDefined();
      expect(defaultConstructor?.visibility).toBe('public');

      const nameConstructor = constructors.find(s => s.signature?.includes('Person(String name)'));
      expect(nameConstructor).toBeDefined();

      const fullConstructor = constructors.find(s => s.signature?.includes('Person(String name, int age)'));
      expect(fullConstructor).toBeDefined();

      const privateConstructor = constructors.find(s => s.signature?.includes('private') && s.signature?.includes('boolean'));
      expect(privateConstructor).toBeDefined();
      expect(privateConstructor?.visibility).toBe('private');
    });
  });

  describe('Field Extraction', () => {
    it('should extract field declarations with modifiers', async () => {
      const javaCode = `
package com.example;

public class Config {
    public static final String VERSION = "1.0.0";
    private String apiKey;
    protected int maxRetries = 3;
    boolean debugMode;
    public final long timestamp = System.currentTimeMillis();

    private static final Logger LOGGER = LoggerFactory.getLogger(Config.class);
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const version = symbols.find(s => s.name === 'VERSION');
      expect(version).toBeDefined();
      expect(version?.kind).toBe(SymbolKind.Constant); // static final = constant
      expect(version?.signature).toContain('public static final String VERSION');
      expect(version?.visibility).toBe('public');

      const apiKey = symbols.find(s => s.name === 'apiKey');
      expect(apiKey).toBeDefined();
      expect(apiKey?.kind).toBe(SymbolKind.Property);
      expect(apiKey?.visibility).toBe('private');

      const maxRetries = symbols.find(s => s.name === 'maxRetries');
      expect(maxRetries).toBeDefined();
      expect(maxRetries?.visibility).toBe('protected');

      const debugMode = symbols.find(s => s.name === 'debugMode');
      expect(debugMode).toBeDefined();
      expect(debugMode?.visibility).toBe('package');

      const timestamp = symbols.find(s => s.name === 'timestamp');
      expect(timestamp).toBeDefined();
      expect(timestamp?.signature).toContain('final');

      const logger = symbols.find(s => s.name === 'LOGGER');
      expect(logger).toBeDefined();
      expect(logger?.kind).toBe(SymbolKind.Constant);
      expect(logger?.visibility).toBe('private');
    });
  });

  describe('Enum Extraction', () => {
    it('should extract enum definitions and values', async () => {
      const javaCode = `
package com.example;

public enum Color {
    RED, GREEN, BLUE
}

public enum Status {
    PENDING("pending"),
    ACTIVE("active"),
    INACTIVE("inactive");

    private final String value;

    Status(String value) {
        this.value = value;
    }

    public String getValue() {
        return value;
    }
}

enum Priority {
    LOW(1), MEDIUM(2), HIGH(3);

    private final int level;

    Priority(int level) {
        this.level = level;
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const colorEnum = symbols.find(s => s.name === 'Color');
      expect(colorEnum).toBeDefined();
      expect(colorEnum?.kind).toBe(SymbolKind.Enum);
      expect(colorEnum?.signature).toContain('public enum Color');
      expect(colorEnum?.visibility).toBe('public');

      const red = symbols.find(s => s.name === 'RED');
      expect(red).toBeDefined();
      expect(red?.kind).toBe(SymbolKind.EnumMember);

      const statusEnum = symbols.find(s => s.name === 'Status');
      expect(statusEnum).toBeDefined();
      expect(statusEnum?.kind).toBe(SymbolKind.Enum);

      const pending = symbols.find(s => s.name === 'PENDING');
      expect(pending).toBeDefined();
      expect(pending?.signature).toContain('PENDING("pending")');

      const priorityEnum = symbols.find(s => s.name === 'Priority');
      expect(priorityEnum).toBeDefined();
      expect(priorityEnum?.visibility).toBe('package'); // no modifier = package
    });
  });

  describe('Annotation Extraction', () => {
    it('should extract annotation definitions', async () => {
      const javaCode = `
package com.example;

import java.lang.annotation.*;

@Retention(RetentionPolicy.RUNTIME)
@Target(ElementType.METHOD)
public @interface Test {
    String value() default "";
    int timeout() default 0;
}

@interface Internal {
    // marker annotation
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const testAnnotation = symbols.find(s => s.name === 'Test');
      expect(testAnnotation).toBeDefined();
      expect(testAnnotation?.kind).toBe(SymbolKind.Interface); // annotations are special interfaces
      expect(testAnnotation?.signature).toContain('@interface Test');
      expect(testAnnotation?.visibility).toBe('public');

      const internalAnnotation = symbols.find(s => s.name === 'Internal');
      expect(internalAnnotation).toBeDefined();
      expect(internalAnnotation?.visibility).toBe('package');
    });
  });

  describe('Generic Types', () => {
    it('should extract generic class and method definitions', async () => {
      const javaCode = `
package com.example;

public class Container<T> {
    private T value;

    public Container(T value) {
        this.value = value;
    }

    public T getValue() {
        return value;
    }

    public <U> U transform(Function<T, U> mapper) {
        return mapper.apply(value);
    }
}

public class Pair<K, V> extends Container<V> {
    private K key;

    public Pair(K key, V value) {
        super(value);
        this.key = key;
    }
}

public interface Repository<T, ID> {
    T findById(ID id);
    List<T> findAll();
    <S extends T> S save(S entity);
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const container = symbols.find(s => s.name === 'Container');
      expect(container).toBeDefined();
      expect(container?.signature).toContain('Container<T>');

      const transform = symbols.find(s => s.name === 'transform');
      expect(transform).toBeDefined();
      expect(transform?.signature).toContain('<U>');

      const pair = symbols.find(s => s.name === 'Pair');
      expect(pair).toBeDefined();
      expect(pair?.signature).toContain('Pair<K, V>');
      expect(pair?.signature).toContain('extends Container<V>');

      const repository = symbols.find(s => s.name === 'Repository');
      expect(repository).toBeDefined();
      expect(repository?.signature).toContain('Repository<T, ID>');

      const save = symbols.find(s => s.name === 'save');
      expect(save).toBeDefined();
      expect(save?.signature).toContain('<S extends T>');
    });
  });

  describe('Nested Classes', () => {
    it('should extract nested and inner classes', async () => {
      const javaCode = `
package com.example;

public class Outer {
    private String name;

    public static class StaticNested {
        public void doSomething() {}
    }

    public class Inner {
        public void accessOuter() {
            System.out.println(name);
        }
    }

    private class PrivateInner {
        private void helper() {}
    }

    public void localClassExample() {
        class LocalClass {
            void localMethod() {}
        }

        LocalClass local = new LocalClass();
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const outer = symbols.find(s => s.name === 'Outer');
      expect(outer).toBeDefined();

      const staticNested = symbols.find(s => s.name === 'StaticNested');
      expect(staticNested).toBeDefined();
      expect(staticNested?.signature).toContain('static class StaticNested');
      expect(staticNested?.visibility).toBe('public');

      const inner = symbols.find(s => s.name === 'Inner');
      expect(inner).toBeDefined();
      expect(inner?.visibility).toBe('public');

      const privateInner = symbols.find(s => s.name === 'PrivateInner');
      expect(privateInner).toBeDefined();
      expect(privateInner?.visibility).toBe('private');

      // Local classes might be harder to extract, but let's test for them
      const localClass = symbols.find(s => s.name === 'LocalClass');
      expect(localClass).toBeDefined();
    });
  });

  describe('Modern Java Features (Java 8+)', () => {
    it('should extract lambda expressions and method references', async () => {
      const javaCode = `
package com.example;

import java.util.*;
import java.util.function.*;
import java.util.stream.*;

public class ModernJavaFeatures {

    // Lambda expressions
    private final Comparator<String> comparator = (s1, s2) -> s1.compareToIgnoreCase(s2);
    private final Function<String, Integer> stringLength = s -> s.length();
    private final BiFunction<Integer, Integer, Integer> sum = (a, b) -> a + b;
    private final Runnable task = () -> System.out.println("Task executed");

    // Method references
    private final Function<String, String> toUpperCase = String::toUpperCase;
    private final Supplier<List<String>> listSupplier = ArrayList::new;
    private final Consumer<String> printer = System.out::println;
    private final BinaryOperator<Integer> max = Integer::max;

    public void streamOperations() {
        List<String> names = Arrays.asList("John", "Jane", "Bob", "Alice");

        // Stream with lambda expressions
        List<String> upperCaseNames = names.stream()
            .filter(name -> name.length() > 3)
            .map(String::toUpperCase)
            .sorted((s1, s2) -> s1.compareTo(s2))
            .collect(Collectors.toList());

        // Parallel stream processing
        Optional<String> longest = names.parallelStream()
            .max(Comparator.comparing(String::length));

        // Complex stream operations
        Map<Integer, List<String>> groupedByLength = names.stream()
            .collect(Collectors.groupingBy(String::length));

        // Stream with reduce operations
        int totalLength = names.stream()
            .mapToInt(String::length)
            .reduce(0, Integer::sum);
    }

    // Optional usage patterns
    public Optional<User> findUser(String name) {
        return Optional.ofNullable(getUserByName(name))
            .filter(user -> user.isActive())
            .map(this::enrichUser);
    }

    public String getUserDisplayName(String userId) {
        return findUser(userId)
            .map(User::getName)
            .map(String::toUpperCase)
            .orElse("Unknown User");
    }

    // Functional interface usage
    @FunctionalInterface
    public interface UserProcessor {
        User process(User user);

        default User processWithLogging(User user) {
            System.out.println("Processing user: " + user.getName());
            return process(user);
        }
    }

    // Higher-order functions
    public <T, R> List<R> transformList(List<T> list, Function<T, R> transformer) {
        return list.stream()
            .map(transformer)
            .collect(Collectors.toList());
    }

    public <T> Optional<T> findFirst(List<T> list, Predicate<T> predicate) {
        return list.stream()
            .filter(predicate)
            .findFirst();
    }

    // CompletableFuture patterns
    public CompletableFuture<String> asyncOperation() {
        return CompletableFuture
            .supplyAsync(() -> fetchDataFromService())
            .thenApply(String::toUpperCase)
            .thenCompose(this::validateData)
            .exceptionally(throwable -> {
                log.error("Error in async operation", throwable);
                return "Default Value";
            });
    }

    public CompletableFuture<Void> combinedAsyncOperations() {
        CompletableFuture<String> future1 = CompletableFuture.supplyAsync(() -> "Data1");
        CompletableFuture<String> future2 = CompletableFuture.supplyAsync(() -> "Data2");

        return CompletableFuture.allOf(future1, future2)
            .thenRun(() -> {
                String result1 = future1.join();
                String result2 = future2.join();
                processResults(result1, result2);
            });
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      const modernFeatures = symbols.find(s => s.name === 'ModernJavaFeatures');
      expect(modernFeatures).toBeDefined();
      expect(modernFeatures?.kind).toBe(SymbolKind.Class);

      // Lambda expression fields
      const comparator = symbols.find(s => s.name === 'comparator');
      expect(comparator).toBeDefined();
      expect(comparator?.signature).toContain('Comparator<String>');

      const stringLength = symbols.find(s => s.name === 'stringLength');
      expect(stringLength).toBeDefined();
      expect(stringLength?.signature).toContain('Function<String, Integer>');

      // Method reference fields
      const toUpperCase = symbols.find(s => s.name === 'toUpperCase');
      expect(toUpperCase).toBeDefined();
      expect(toUpperCase?.signature).toContain('Function<String, String>');

      const printer = symbols.find(s => s.name === 'printer');
      expect(printer).toBeDefined();
      expect(printer?.signature).toContain('Consumer<String>');

      // Stream operations method
      const streamOperations = symbols.find(s => s.name === 'streamOperations');
      expect(streamOperations).toBeDefined();
      expect(streamOperations?.kind).toBe(SymbolKind.Method);

      // Optional methods
      const findUser = symbols.find(s => s.name === 'findUser');
      expect(findUser).toBeDefined();
      expect(findUser?.signature).toContain('Optional<User>');

      const getUserDisplayName = symbols.find(s => s.name === 'getUserDisplayName');
      expect(getUserDisplayName).toBeDefined();
      expect(getUserDisplayName?.signature).toContain('String getUserDisplayName');

      // Functional interface
      const userProcessor = symbols.find(s => s.name === 'UserProcessor');
      expect(userProcessor).toBeDefined();
      expect(userProcessor?.kind).toBe(SymbolKind.Interface);
      expect(userProcessor?.signature).toContain('@FunctionalInterface');

      // Generic methods
      const transformList = symbols.find(s => s.name === 'transformList');
      expect(transformList).toBeDefined();
      expect(transformList?.signature).toContain('<T, R>');

      const asyncOperation = symbols.find(s => s.name === 'asyncOperation');
      expect(asyncOperation).toBeDefined();
      expect(asyncOperation?.signature).toContain('CompletableFuture<String>');
    });
  });

  describe('Advanced Language Features', () => {
    it('should extract records, sealed classes, and pattern matching', async () => {
      const javaCode = `
package com.example;

import java.util.*;

// Record types (Java 14+)
public record Person(String name, int age, String email) {
    // Compact constructor
    public Person {
        if (age < 0) {
            throw new IllegalArgumentException("Age cannot be negative");
        }
        if (name == null || name.isBlank()) {
            throw new IllegalArgumentException("Name cannot be null or blank");
        }
    }

    // Custom constructor
    public Person(String name, int age) {
        this(name, age, name.toLowerCase() + "@example.com");
    }

    // Instance methods in records
    public boolean isAdult() {
        return age >= 18;
    }

    public String getDisplayName() {
        return name + " (" + age + ")";
    }
}

// Sealed classes (Java 17+)
public sealed class Shape
    permits Circle, Rectangle, Triangle {

    public abstract double area();
    public abstract double perimeter();
}

public final class Circle extends Shape {
    private final double radius;

    public Circle(double radius) {
        this.radius = radius;
    }

    @Override
    public double area() {
        return Math.PI * radius * radius;
    }

    @Override
    public double perimeter() {
        return 2 * Math.PI * radius;
    }

    public double radius() {
        return radius;
    }
}

public final class Rectangle extends Shape {
    private final double width;
    private final double height;

    public Rectangle(double width, double height) {
        this.width = width;
        this.height = height;
    }

    @Override
    public double area() {
        return width * height;
    }

    @Override
    public double perimeter() {
        return 2 * (width + height);
    }

    public double width() { return width; }
    public double height() { return height; }
}

public non-sealed class Triangle extends Shape {
    private final double a, b, c;

    public Triangle(double a, double b, double c) {
        this.a = a;
        this.b = b;
        this.c = c;
    }

    @Override
    public double area() {
        double s = (a + b + c) / 2;
        return Math.sqrt(s * (s - a) * (s - b) * (s - c));
    }

    @Override
    public double perimeter() {
        return a + b + c;
    }
}

// Pattern matching and switch expressions
public class PatternMatching {

    // Switch expressions (Java 14+)
    public String describeShape(Shape shape) {
        return switch (shape) {
            case Circle c -> "Circle with radius " + c.radius();
            case Rectangle r -> "Rectangle " + r.width() + "x" + r.height();
            case Triangle t -> "Triangle with perimeter " + t.perimeter();
        };
    }

    // Pattern matching with instanceof (Java 16+)
    public double calculateShapeArea(Object obj) {
        if (obj instanceof Circle c) {
            return c.area();
        } else if (obj instanceof Rectangle r) {
            return r.area();
        } else if (obj instanceof Triangle t) {
            return t.area();
        } else {
            throw new IllegalArgumentException("Unknown shape type");
        }
    }

    // Text blocks (Java 13+)
    public String getJsonTemplate() {
        return """
            {
                "name": "%s",
                "age": %d,
                "email": "%s",
                "active": %b
            }
            """;
    }

    public String getSqlQuery() {
        return """
            SELECT p.name, p.age, p.email
            FROM person p
            WHERE p.age >= 18
              AND p.active = true
            ORDER BY p.name
            """;
    }

    // Switch with multiple cases
    public String categorizeAge(int age) {
        return switch (age) {
            case 0, 1, 2 -> "Baby";
            case 3, 4, 5 -> "Toddler";
            case 6, 7, 8, 9, 10, 11, 12 -> "Child";
            case 13, 14, 15, 16, 17 -> "Teenager";
            default -> {
                if (age >= 18 && age < 65) {
                    yield "Adult";
                } else if (age >= 65) {
                    yield "Senior";
                } else {
                    yield "Invalid age";
                }
            }
        };
    }

    // Enhanced instanceof with pattern variables
    public void processObject(Object obj) {
        if (obj instanceof String str && str.length() > 5) {
            System.out.println("Long string: " + str.toUpperCase());
        } else if (obj instanceof Integer num && num > 0) {
            System.out.println("Positive number: " + num);
        } else if (obj instanceof List<?> list && !list.isEmpty()) {
            System.out.println("Non-empty list with " + list.size() + " elements");
        }
    }
}

// Record with generics
public record Result<T, E>(T value, E error, boolean isSuccess) {

    public static <T, E> Result<T, E> success(T value) {
        return new Result<>(value, null, true);
    }

    public static <T, E> Result<T, E> failure(E error) {
        return new Result<>(null, error, false);
    }

    public Optional<T> getValue() {
        return isSuccess ? Optional.of(value) : Optional.empty();
    }

    public Optional<E> getError() {
        return !isSuccess ? Optional.of(error) : Optional.empty();
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Record type
      const person = symbols.find(s => s.name === 'Person');
      expect(person).toBeDefined();
      expect(person?.kind).toBe(SymbolKind.Class);
      expect(person?.signature).toContain('record Person');

      const personCompactConstructor = symbols.find(s => s.name === 'Person' && s.kind === SymbolKind.Constructor);
      expect(personCompactConstructor).toBeDefined();

      const isAdult = symbols.find(s => s.name === 'isAdult');
      expect(isAdult).toBeDefined();
      expect(isAdult?.signature).toContain('boolean isAdult()');

      // Sealed class
      const shape = symbols.find(s => s.name === 'Shape');
      expect(shape).toBeDefined();
      expect(shape?.signature).toContain('sealed class Shape');
      expect(shape?.signature).toContain('permits Circle, Rectangle, Triangle');

      const circle = symbols.find(s => s.name === 'Circle');
      expect(circle).toBeDefined();
      expect(circle?.signature).toContain('final class Circle extends Shape');

      const rectangle = symbols.find(s => s.name === 'Rectangle');
      expect(rectangle).toBeDefined();
      expect(rectangle?.signature).toContain('final class Rectangle extends Shape');

      const triangle = symbols.find(s => s.name === 'Triangle');
      expect(triangle).toBeDefined();
      expect(triangle?.signature).toContain('non-sealed class Triangle extends Shape');

      // Pattern matching class
      const patternMatching = symbols.find(s => s.name === 'PatternMatching');
      expect(patternMatching).toBeDefined();

      const describeShape = symbols.find(s => s.name === 'describeShape');
      expect(describeShape).toBeDefined();
      expect(describeShape?.signature).toContain('String describeShape(Shape shape)');

      const calculateShapeArea = symbols.find(s => s.name === 'calculateShapeArea');
      expect(calculateShapeArea).toBeDefined();
      expect(calculateShapeArea?.signature).toContain('double calculateShapeArea(Object obj)');

      const getJsonTemplate = symbols.find(s => s.name === 'getJsonTemplate');
      expect(getJsonTemplate).toBeDefined();
      expect(getJsonTemplate?.signature).toContain('String getJsonTemplate()');

      const categorizeAge = symbols.find(s => s.name === 'categorizeAge');
      expect(categorizeAge).toBeDefined();
      expect(categorizeAge?.signature).toContain('String categorizeAge(int age)');

      // Generic record
      const resultRecord = symbols.find(s => s.name === 'Result');
      expect(resultRecord).toBeDefined();
      expect(resultRecord?.signature).toContain('record Result<T, E>');

      const successMethod = symbols.find(s => s.name === 'success');
      expect(successMethod).toBeDefined();
      expect(successMethod?.signature).toContain('static <T, E> Result<T, E> success');
    });
  });

  describe('Exception Handling and Resource Management', () => {
    it('should extract exception classes and try-with-resources', async () => {
      const javaCode = `
package com.example;

import java.io.*;
import java.util.*;
import java.sql.*;

// Custom exception hierarchy
public class BusinessException extends Exception {
    private final String errorCode;
    private final Map<String, Object> context;

    public BusinessException(String message, String errorCode) {
        super(message);
        this.errorCode = errorCode;
        this.context = new HashMap<>();
    }

    public BusinessException(String message, String errorCode, Throwable cause) {
        super(message, cause);
        this.errorCode = errorCode;
        this.context = new HashMap<>();
    }

    public BusinessException addContext(String key, Object value) {
        this.context.put(key, value);
        return this;
    }

    public String getErrorCode() {
        return errorCode;
    }

    public Map<String, Object> getContext() {
        return Collections.unmodifiableMap(context);
    }
}

public class ValidationException extends BusinessException {
    private final List<String> violations;

    public ValidationException(String message, List<String> violations) {
        super(message, "VALIDATION_ERROR");
        this.violations = new ArrayList<>(violations);
    }

    public List<String> getViolations() {
        return Collections.unmodifiableList(violations);
    }
}

public class DataAccessException extends BusinessException {
    public DataAccessException(String message, Throwable cause) {
        super(message, "DATA_ACCESS_ERROR", cause);
    }
}

// Resource management with try-with-resources
public class ResourceManager {

    // Single resource
    public String readFile(String fileName) throws IOException {
        try (BufferedReader reader = Files.newBufferedReader(Paths.get(fileName))) {
            return reader.lines()
                .collect(Collectors.joining("\n"));
        }
    }

    // Multiple resources
    public void copyFile(String source, String destination) throws IOException {
        try (InputStream input = Files.newInputStream(Paths.get(source));
             OutputStream output = Files.newOutputStream(Paths.get(destination))) {

            byte[] buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = input.read(buffer)) != -1) {
                output.write(buffer, 0, bytesRead);
            }
        }
    }

    // Database operations with try-with-resources
    public List<User> getUsersFromDatabase(String connectionUrl) throws DataAccessException {
        try (Connection connection = DriverManager.getConnection(connectionUrl);
             PreparedStatement statement = connection.prepareStatement(
                 "SELECT id, name, email FROM users WHERE active = ?");
        ) {
            statement.setBoolean(1, true);

            try (ResultSet resultSet = statement.executeQuery()) {
                List<User> users = new ArrayList<>();
                while (resultSet.next()) {
                    User user = new User(
                        resultSet.getLong("id"),
                        resultSet.getString("name"),
                        resultSet.getString("email")
                    );
                    users.add(user);
                }
                return users;
            }
        } catch (SQLException e) {
            throw new DataAccessException("Failed to fetch users from database", e);
        }
    }

    // Multi-catch exception handling
    public void processData(String data) throws BusinessException {
        try {
            validateInput(data);
            parseData(data);
            persistData(data);
        } catch (IllegalArgumentException | NumberFormatException e) {
            throw new ValidationException("Invalid data format",
                Arrays.asList(e.getMessage()));
        } catch (IOException | SQLException e) {
            throw new DataAccessException("Failed to process data", e);
        } catch (Exception e) {
            throw new BusinessException("Unexpected error during data processing",
                "PROCESSING_ERROR", e);
        }
    }

    // Exception handling with suppressed exceptions
    public void closeResources(AutoCloseable... resources) {
        Exception primaryException = null;

        for (AutoCloseable resource : resources) {
            try {
                if (resource != null) {
                    resource.close();
                }
            } catch (Exception e) {
                if (primaryException == null) {
                    primaryException = e;
                } else {
                    primaryException.addSuppressed(e);
                }
            }
        }

        if (primaryException != null) {
            if (primaryException instanceof RuntimeException) {
                throw (RuntimeException) primaryException;
            } else {
                throw new RuntimeException(primaryException);
            }
        }
    }

    // Exception chaining and wrapping
    public void chainedExceptionExample() throws BusinessException {
        try {
            riskyOperation();
        } catch (IOException e) {
            DataAccessException dae = new DataAccessException("Database operation failed", e);
            dae.addContext("operation", "chainedExceptionExample");
            dae.addContext("timestamp", Instant.now());
            throw dae;
        }
    }

    // Custom resource implementation
    public static class ManagedResource implements AutoCloseable {
        private final String resourceName;
        private boolean closed = false;

        public ManagedResource(String resourceName) {
            this.resourceName = resourceName;
            System.out.println("Opening resource: " + resourceName);
        }

        public void doWork() throws IOException {
            if (closed) {
                throw new IllegalStateException("Resource is closed");
            }
            // Simulate work
            if (Math.random() < 0.1) {
                throw new IOException("Random failure in " + resourceName);
            }
        }

        @Override
        public void close() throws IOException {
            if (!closed) {
                System.out.println("Closing resource: " + resourceName);
                closed = true;
                // Simulate close failure
                if (Math.random() < 0.05) {
                    throw new IOException("Failed to close " + resourceName);
                }
            }
        }
    }

    // Complex try-with-resources with custom resource
    public void complexResourceManagement() throws BusinessException {
        try (ManagedResource resource1 = new ManagedResource("Database");
             ManagedResource resource2 = new ManagedResource("FileSystem");
             ManagedResource resource3 = new ManagedResource("Network")) {

            resource1.doWork();
            resource2.doWork();
            resource3.doWork();

        } catch (IOException e) {
            throw new BusinessException("Resource operation failed",
                "RESOURCE_ERROR", e)
                .addContext("resources", Arrays.asList("Database", "FileSystem", "Network"));
        }
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Custom exceptions
      const businessException = symbols.find(s => s.name === 'BusinessException');
      expect(businessException).toBeDefined();
      expect(businessException?.kind).toBe(SymbolKind.Class);
      expect(businessException?.signature).toContain('class BusinessException extends Exception');

      const validationException = symbols.find(s => s.name === 'ValidationException');
      expect(validationException).toBeDefined();
      expect(validationException?.signature).toContain('class ValidationException extends BusinessException');

      const dataAccessException = symbols.find(s => s.name === 'DataAccessException');
      expect(dataAccessException).toBeDefined();
      expect(dataAccessException?.signature).toContain('class DataAccessException extends BusinessException');

      // Exception constructors
      const businessExceptionConstructor = symbols.find(s =>
        s.name === 'BusinessException' &&
        s.kind === SymbolKind.Constructor &&
        s.signature?.includes('String message, String errorCode, Throwable cause')
      );
      expect(businessExceptionConstructor).toBeDefined();

      // Exception methods
      const addContext = symbols.find(s => s.name === 'addContext');
      expect(addContext).toBeDefined();
      expect(addContext?.signature).toContain('BusinessException addContext(String key, Object value)');

      const getErrorCode = symbols.find(s => s.name === 'getErrorCode');
      expect(getErrorCode).toBeDefined();
      expect(getErrorCode?.signature).toContain('String getErrorCode()');

      // Resource manager
      const resourceManager = symbols.find(s => s.name === 'ResourceManager');
      expect(resourceManager).toBeDefined();
      expect(resourceManager?.kind).toBe(SymbolKind.Class);

      const readFile = symbols.find(s => s.name === 'readFile');
      expect(readFile).toBeDefined();
      expect(readFile?.signature).toContain('String readFile(String fileName) throws IOException');

      const copyFile = symbols.find(s => s.name === 'copyFile');
      expect(copyFile).toBeDefined();
      expect(copyFile?.signature).toContain('void copyFile(String source, String destination) throws IOException');

      const getUsersFromDatabase = symbols.find(s => s.name === 'getUsersFromDatabase');
      expect(getUsersFromDatabase).toBeDefined();
      expect(getUsersFromDatabase?.signature).toContain('List<User> getUsersFromDatabase(String connectionUrl) throws DataAccessException');

      const processData = symbols.find(s => s.name === 'processData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('void processData(String data) throws BusinessException');

      const closeResources = symbols.find(s => s.name === 'closeResources');
      expect(closeResources).toBeDefined();
      expect(closeResources?.signature).toContain('void closeResources(AutoCloseable... resources)');

      // Custom resource
      const managedResource = symbols.find(s => s.name === 'ManagedResource');
      expect(managedResource).toBeDefined();
      expect(managedResource?.signature).toContain('static class ManagedResource implements AutoCloseable');

      const doWork = symbols.find(s => s.name === 'doWork');
      expect(doWork).toBeDefined();
      expect(doWork?.signature).toContain('void doWork() throws IOException');

      const close = symbols.find(s => s.name === 'close');
      expect(close).toBeDefined();
      expect(close?.signature).toContain('@Override');
      expect(close?.signature).toContain('void close() throws IOException');
    });
  });

  describe('Testing Patterns and Annotations', () => {
    it('should extract JUnit and testing framework patterns', async () => {
      const javaCode = `
package com.example.test;

import org.junit.jupiter.api.*;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.*;
import org.mockito.*;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.TestPropertySource;

import static org.junit.jupiter.api.Assertions.*;
import static org.mockito.Mockito.*;

@SpringBootTest
@TestPropertySource(locations = "classpath:test.properties")
@TestMethodOrder(OrderAnnotation.class)
public class UserServiceTest {

    @Mock
    private UserRepository userRepository;

    @InjectMocks
    private UserService userService;

    @Captor
    private ArgumentCaptor<User> userCaptor;

    private static TestDataBuilder testDataBuilder;

    @BeforeAll
    static void setUpClass() {
        testDataBuilder = new TestDataBuilder();
        System.out.println("Setting up test class");
    }

    @AfterAll
    static void tearDownClass() {
        testDataBuilder = null;
        System.out.println("Tearing down test class");
    }

    @BeforeEach
    void setUp() {
        MockitoAnnotations.openMocks(this);
        System.out.println("Setting up test method");
    }

    @AfterEach
    void tearDown() {
        reset(userRepository);
        System.out.println("Tearing down test method");
    }

    @Test
    @DisplayName("Should create user successfully")
    @Order(1)
    void shouldCreateUserSuccessfully() {
        // Given
        User newUser = testDataBuilder.createUser("John Doe", "john@example.com");
        when(userRepository.save(any(User.class))).thenReturn(newUser);

        // When
        User createdUser = userService.createUser(newUser);

        // Then
        assertNotNull(createdUser);
        assertEquals("John Doe", createdUser.getName());
        assertEquals("john@example.com", createdUser.getEmail());

        verify(userRepository).save(userCaptor.capture());
        User capturedUser = userCaptor.getValue();
        assertEquals(newUser.getName(), capturedUser.getName());
    }

    @Test
    @DisplayName("Should throw exception when user is null")
    @Order(2)
    void shouldThrowExceptionWhenUserIsNull() {
        // When & Then
        assertThrows(IllegalArgumentException.class, () -> {
            userService.createUser(null);
        });

        verifyNoInteractions(userRepository);
    }

    @ParameterizedTest
    @DisplayName("Should validate email format")
    @ValueSource(strings = {"invalid", "@invalid.com", "invalid@", ""})
    void shouldValidateEmailFormat(String invalidEmail) {
        // Given
        User user = testDataBuilder.createUser("John Doe", invalidEmail);

        // When & Then
        assertThrows(ValidationException.class, () -> {
            userService.createUser(user);
        });
    }

    @ParameterizedTest
    @DisplayName("Should accept valid email formats")
    @ValueSource(strings = {
        "test@example.com",
        "user.name@domain.co.uk",
        "first.last+tag@example.org"
    })
    void shouldAcceptValidEmailFormats(String validEmail) {
        // Given
        User user = testDataBuilder.createUser("John Doe", validEmail);
        when(userRepository.save(any(User.class))).thenReturn(user);

        // When & Then
        assertDoesNotThrow(() -> {
            userService.createUser(user);
        });
    }

    @ParameterizedTest
    @DisplayName("Should handle different user scenarios")
    @MethodSource("userTestCases")
    void shouldHandleDifferentUserScenarios(UserTestCase testCase) {
        // Given
        when(userRepository.save(any(User.class))).thenReturn(testCase.expectedUser());

        // When
        User result = userService.createUser(testCase.inputUser());

        // Then
        assertEquals(testCase.expectedUser().getName(), result.getName());
        assertEquals(testCase.expectedUser().getEmail(), result.getEmail());
    }

    static Stream<UserTestCase> userTestCases() {
        return Stream.of(
            new UserTestCase(
                testDataBuilder.createUser("Alice", "alice@example.com"),
                testDataBuilder.createUser("Alice", "alice@example.com")
            ),
            new UserTestCase(
                testDataBuilder.createUser("Bob", "bob@test.org"),
                testDataBuilder.createUser("Bob", "bob@test.org")
            )
        );
    }

    @Test
    @DisplayName("Should find user by id")
    @Timeout(value = 2, unit = TimeUnit.SECONDS)
    void shouldFindUserById() {
        // Given
        Long userId = 1L;
        User expectedUser = testDataBuilder.createUser("John", "john@example.com");
        when(userRepository.findById(userId)).thenReturn(Optional.of(expectedUser));

        // When
        Optional<User> result = userService.findById(userId);

        // Then
        assertTrue(result.isPresent());
        assertEquals(expectedUser.getName(), result.get().getName());
    }

    @RepeatedTest(value = 5, name = "Execution {currentRepetition} of {totalRepetitions}")
    @DisplayName("Should handle concurrent user creation")
    void shouldHandleConcurrentUserCreation(RepetitionInfo repetitionInfo) {
        // Given
        String userName = "User" + repetitionInfo.getCurrentRepetition();
        User user = testDataBuilder.createUser(userName, userName.toLowerCase() + "@example.com");
        when(userRepository.save(any(User.class))).thenReturn(user);

        // When
        User result = userService.createUser(user);

        // Then
        assertNotNull(result);
        assertEquals(userName, result.getName());
    }

    @Test
    @DisplayName("Should handle database exceptions gracefully")
    @Tag("integration")
    void shouldHandleDatabaseExceptionsGracefully() {
        // Given
        User user = testDataBuilder.createUser("John", "john@example.com");
        when(userRepository.save(any(User.class)))
            .thenThrow(new DataAccessException("Database connection failed"));

        // When & Then
        assertThrows(ServiceException.class, () -> {
            userService.createUser(user);
        });
    }

    @Nested
    @DisplayName("User validation tests")
    class UserValidationTests {

        @Test
        @DisplayName("Should validate required fields")
        void shouldValidateRequiredFields() {
            // Test implementation
            assertAll(
                () -> assertThrows(ValidationException.class,
                    () -> userService.createUser(new User(null, "test@example.com"))),
                () -> assertThrows(ValidationException.class,
                    () -> userService.createUser(new User("John", null))),
                () -> assertThrows(ValidationException.class,
                    () -> userService.createUser(new User("", "test@example.com")))
            );
        }

        @Test
        @DisplayName("Should validate business rules")
        void shouldValidateBusinessRules() {
            // Test implementation for business rule validation
            User user = testDataBuilder.createUser("ValidUser", "valid@example.com");

            assertDoesNotThrow(() -> {
                userService.validateBusinessRules(user);
            });
        }
    }

    // Helper classes and records
    public record UserTestCase(User inputUser, User expectedUser) {}

    @TestConfiguration
    static class TestConfig {

        @Bean
        @Primary
        public Clock testClock() {
            return Clock.fixed(Instant.parse("2023-01-01T00:00:00Z"), ZoneOffset.UTC);
        }
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Test class
      const userServiceTest = symbols.find(s => s.name === 'UserServiceTest');
      expect(userServiceTest).toBeDefined();
      expect(userServiceTest?.kind).toBe(SymbolKind.Class);
      expect(userServiceTest?.signature).toContain('@SpringBootTest');
      expect(userServiceTest?.signature).toContain('@TestPropertySource');
      expect(userServiceTest?.signature).toContain('@TestMethodOrder');

      // Mock fields
      const userRepository = symbols.find(s => s.name === 'userRepository');
      expect(userRepository).toBeDefined();
      expect(userRepository?.signature).toContain('@Mock');

      const userService = symbols.find(s => s.name === 'userService');
      expect(userService).toBeDefined();
      expect(userService?.signature).toContain('@InjectMocks');

      const userCaptor = symbols.find(s => s.name === 'userCaptor');
      expect(userCaptor).toBeDefined();
      expect(userCaptor?.signature).toContain('@Captor');

      // Lifecycle methods
      const setUpClass = symbols.find(s => s.name === 'setUpClass');
      expect(setUpClass).toBeDefined();
      expect(setUpClass?.signature).toContain('@BeforeAll');
      expect(setUpClass?.signature).toContain('static void setUpClass()');

      const tearDownClass = symbols.find(s => s.name === 'tearDownClass');
      expect(tearDownClass).toBeDefined();
      expect(tearDownClass?.signature).toContain('@AfterAll');

      const setUp = symbols.find(s => s.name === 'setUp');
      expect(setUp).toBeDefined();
      expect(setUp?.signature).toContain('@BeforeEach');

      const tearDown = symbols.find(s => s.name === 'tearDown');
      expect(tearDown).toBeDefined();
      expect(tearDown?.signature).toContain('@AfterEach');

      // Test methods
      const shouldCreateUserSuccessfully = symbols.find(s => s.name === 'shouldCreateUserSuccessfully');
      expect(shouldCreateUserSuccessfully).toBeDefined();
      expect(shouldCreateUserSuccessfully?.signature).toContain('@Test');
      expect(shouldCreateUserSuccessfully?.signature).toContain('@DisplayName("Should create user successfully")');
      expect(shouldCreateUserSuccessfully?.signature).toContain('@Order(1)');

      const shouldThrowExceptionWhenUserIsNull = symbols.find(s => s.name === 'shouldThrowExceptionWhenUserIsNull');
      expect(shouldThrowExceptionWhenUserIsNull).toBeDefined();
      expect(shouldThrowExceptionWhenUserIsNull?.signature).toContain('@Test');

      // Parameterized tests
      const shouldValidateEmailFormat = symbols.find(s => s.name === 'shouldValidateEmailFormat');
      expect(shouldValidateEmailFormat).toBeDefined();
      expect(shouldValidateEmailFormat?.signature).toContain('@ParameterizedTest');
      expect(shouldValidateEmailFormat?.signature).toContain('@ValueSource');

      const shouldAcceptValidEmailFormats = symbols.find(s => s.name === 'shouldAcceptValidEmailFormats');
      expect(shouldAcceptValidEmailFormats).toBeDefined();
      expect(shouldAcceptValidEmailFormats?.signature).toContain('@ParameterizedTest');

      const shouldHandleDifferentUserScenarios = symbols.find(s => s.name === 'shouldHandleDifferentUserScenarios');
      expect(shouldHandleDifferentUserScenarios).toBeDefined();
      expect(shouldHandleDifferentUserScenarios?.signature).toContain('@MethodSource("userTestCases")');

      // Test data methods
      const userTestCases = symbols.find(s => s.name === 'userTestCases');
      expect(userTestCases).toBeDefined();
      expect(userTestCases?.signature).toContain('static Stream<UserTestCase> userTestCases()');

      // Timeout test
      const shouldFindUserById = symbols.find(s => s.name === 'shouldFindUserById');
      expect(shouldFindUserById).toBeDefined();
      expect(shouldFindUserById?.signature).toContain('@Timeout');

      // Repeated test
      const shouldHandleConcurrentUserCreation = symbols.find(s => s.name === 'shouldHandleConcurrentUserCreation');
      expect(shouldHandleConcurrentUserCreation).toBeDefined();
      expect(shouldHandleConcurrentUserCreation?.signature).toContain('@RepeatedTest');

      // Tagged test
      const shouldHandleDatabaseExceptionsGracefully = symbols.find(s => s.name === 'shouldHandleDatabaseExceptionsGracefully');
      expect(shouldHandleDatabaseExceptionsGracefully).toBeDefined();
      expect(shouldHandleDatabaseExceptionsGracefully?.signature).toContain('@Tag("integration")');

      // Nested test class
      const userValidationTests = symbols.find(s => s.name === 'UserValidationTests');
      expect(userValidationTests).toBeDefined();
      expect(userValidationTests?.signature).toContain('@Nested');
      expect(userValidationTests?.signature).toContain('@DisplayName("User validation tests")');

      // Record for test data
      const userTestCase = symbols.find(s => s.name === 'UserTestCase');
      expect(userTestCase).toBeDefined();
      expect(userTestCase?.signature).toContain('record UserTestCase');

      // Test configuration
      const testConfig = symbols.find(s => s.name === 'TestConfig');
      expect(testConfig).toBeDefined();
      expect(testConfig?.signature).toContain('@TestConfiguration');
      expect(testConfig?.signature).toContain('static class TestConfig');
    });
  });

  describe('Java-specific Features', () => {
    it('should handle comprehensive Java code', async () => {
      const javaCode = `
package com.example.service;

import java.util.*;
import java.util.concurrent.CompletableFuture;
import static java.util.stream.Collectors.*;

/**
 * User service for managing user operations
 */
@Service
@Transactional
public class UserService implements CrudService<User, Long> {

    @Autowired
    private UserRepository repository;

    @Value("\${app.default.timeout}")
    private int timeout;

    public static final String DEFAULT_ROLE = "USER";

    @Override
    public User findById(Long id) {
        return repository.findById(id)
            .orElseThrow(() -> new UserNotFoundException("User not found: " + id));
    }

    @Async
    public CompletableFuture<List<User>> findActiveUsers() {
        return CompletableFuture.supplyAsync(() ->
            repository.findAll().stream()
                .filter(User::isActive)
                .collect(toList())
        );
    }

    @PreAuthorize("hasRole('ADMIN')")
    public void deleteUser(Long id) {
        repository.deleteById(id);
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Check we extracted all major symbols
      expect(symbols.find(s => s.name === 'com.example.service')).toBeDefined();
      expect(symbols.find(s => s.name === 'UserService')).toBeDefined();
      expect(symbols.find(s => s.name === 'repository')).toBeDefined();
      expect(symbols.find(s => s.name === 'timeout')).toBeDefined();
      expect(symbols.find(s => s.name === 'DEFAULT_ROLE')).toBeDefined();
      expect(symbols.find(s => s.name === 'findById')).toBeDefined();
      expect(symbols.find(s => s.name === 'findActiveUsers')).toBeDefined();
      expect(symbols.find(s => s.name === 'deleteUser')).toBeDefined();

      // Check specific features
      const userService = symbols.find(s => s.name === 'UserService');
      expect(userService?.signature).toContain('implements CrudService<User, Long>');

      const findByIdMethod = symbols.find(s => s.name === 'findById');
      expect(findByIdMethod?.signature).toContain('@Override');

      const findActiveUsers = symbols.find(s => s.name === 'findActiveUsers');
      expect(findActiveUsers?.signature).toContain('@Async');

      const deleteUser = symbols.find(s => s.name === 'deleteUser');
      expect(deleteUser?.signature).toContain("@PreAuthorize");

      console.log(`☕ Extracted ${symbols.length} Java symbols successfully`);
    });
  });

  describe('Performance and Edge Cases', () => {
    it('should handle large Java files with many symbols', async () => {
      // Generate a large Java file with many classes and methods
      const services = Array.from({ length: 15 }, (_, i) => `
/**
 * Service class for Service${i}
 */
@Service
@Transactional
public class Service${i} {

    @Autowired
    private Repository${i} repository;

    @Value("\${service${i}.timeout:30}")
    private int timeout;

    public static final String SERVICE_NAME = "Service${i}";

    public List<Entity${i}> findAll() {
        return repository.findAll();
    }

    public Optional<Entity${i}> findById(Long id) {
        return repository.findById(id);
    }

    @Async
    public CompletableFuture<Entity${i}> createAsync(Entity${i} entity) {
        return CompletableFuture.supplyAsync(() -> repository.save(entity));
    }

    @PreAuthorize("hasRole('ADMIN')")
    public void delete(Long id) {
        repository.deleteById(id);
    }
}`).join('\n');

      const entities = Array.from({ length: 15 }, (_, i) => `
@Entity
@Table(name = "entity_${i}")
public class Entity${i} {

    @Id
    @GeneratedValue(strategy = GenerationType.IDENTITY)
    private Long id;

    @Column(nullable = false)
    private String name;

    @Column
    private String description;

    @CreatedDate
    private LocalDateTime createdAt;

    @LastModifiedDate
    private LocalDateTime updatedAt;

    // Default constructor
    public Entity${i}() {}

    // Constructor with name
    public Entity${i}(String name) {
        this.name = name;
    }

    // Getters and setters
    public Long getId() { return id; }
    public void setId(Long id) { this.id = id; }

    public String getName() { return name; }
    public void setName(String name) { this.name = name; }

    public String getDescription() { return description; }
    public void setDescription(String description) { this.description = description; }

    public LocalDateTime getCreatedAt() { return createdAt; }
    public void setCreatedAt(LocalDateTime createdAt) { this.createdAt = createdAt; }

    public LocalDateTime getUpdatedAt() { return updatedAt; }
    public void setUpdatedAt(LocalDateTime updatedAt) { this.updatedAt = updatedAt; }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (obj == null || getClass() != obj.getClass()) return false;
        Entity${i} entity = (Entity${i}) obj;
        return Objects.equals(id, entity.id);
    }

    @Override
    public int hashCode() {
        return Objects.hash(id);
    }

    @Override
    public String toString() {
        return "Entity${i}{" +
            "id=" + id +
            ", name='" + name + '\'' +
            ", description='" + description + '\'' +
            '}';
    }
}`).join('\n');

      const javaCode = `
package com.example.large;

import java.util.*;
import java.time.LocalDateTime;
import java.util.concurrent.CompletableFuture;
import javax.persistence.*;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.scheduling.annotation.Async;
import org.springframework.security.access.prepost.PreAuthorize;
import org.springframework.data.annotation.CreatedDate;
import org.springframework.data.annotation.LastModifiedDate;
import static java.util.stream.Collectors.*;

// Constants
public final class Constants {
    public static final String APPLICATION_NAME = "Large Application";
    public static final String VERSION = "1.0.0";
    public static final int MAX_CONNECTIONS = 100;
    public static final long TIMEOUT_SECONDS = 30L;

    private Constants() {
        // Utility class
    }
}

// Configuration
@Configuration
@EnableJpaRepositories
@EnableAsync
public class ApplicationConfig {

    @Bean
    public TaskExecutor taskExecutor() {
        ThreadPoolTaskExecutor executor = new ThreadPoolTaskExecutor();
        executor.setCorePoolSize(10);
        executor.setMaxPoolSize(20);
        executor.setQueueCapacity(200);
        executor.setThreadNamePrefix("app-");
        executor.initialize();
        return executor;
    }

    @Bean
    public RestTemplate restTemplate() {
        return new RestTemplate();
    }
}

${entities}
${services}

// Main application class
@SpringBootApplication
@EnableScheduling
@EnableCaching
public class LargeApplication {

    private static final Logger log = LoggerFactory.getLogger(LargeApplication.class);

    public static void main(String[] args) {
        log.info("Starting application: {}", Constants.APPLICATION_NAME);
        SpringApplication.run(LargeApplication.class, args);
        log.info("Application started successfully");
    }
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should extract many symbols
      expect(symbols.length).toBeGreaterThan(200);

      // Check that all generated services were extracted
      for (let i = 0; i < 15; i++) {
        const service = symbols.find(s => s.name === `Service${i}`);
        expect(service).toBeDefined();
        expect(service?.kind).toBe(SymbolKind.Class);
        expect(service?.signature).toContain('@Service');
      }

      // Check that all entities were extracted
      for (let i = 0; i < 15; i++) {
        const entity = symbols.find(s => s.name === `Entity${i}`);
        expect(entity).toBeDefined();
        expect(entity?.kind).toBe(SymbolKind.Class);
        expect(entity?.signature).toContain('@Entity');
      }

      // Check constants class
      const constants = symbols.find(s => s.name === 'Constants');
      expect(constants).toBeDefined();
      expect(constants?.signature).toContain('final class Constants');

      const applicationName = symbols.find(s => s.name === 'APPLICATION_NAME');
      expect(applicationName).toBeDefined();
      expect(applicationName?.kind).toBe(SymbolKind.Constant);

      // Check configuration
      const appConfig = symbols.find(s => s.name === 'ApplicationConfig');
      expect(appConfig).toBeDefined();
      expect(appConfig?.signature).toContain('@Configuration');

      // Check main application
      const largeApplication = symbols.find(s => s.name === 'LargeApplication');
      expect(largeApplication).toBeDefined();
      expect(largeApplication?.signature).toContain('@SpringBootApplication');

      const mainMethod = symbols.find(s => s.name === 'main');
      expect(mainMethod).toBeDefined();
      expect(mainMethod?.signature).toContain('static void main(String[] args)');

      console.log(`☕ Performance test: Extracted ${symbols.length} symbols and ${relationships.length} relationships`);
    });

    it('should handle edge cases and malformed code gracefully', async () => {
      const javaCode = `
package com.example.edge;

// Edge cases and unusual Java constructs

// Empty classes and interfaces
public class EmptyClass {}
public interface EmptyInterface {}
public abstract class EmptyAbstractClass {}

// Classes with only static members
public final class UtilityClass {
    private UtilityClass() {}

    public static void utilityMethod() {}
    public static final String CONSTANT = "value";
}

// Deeply nested classes
public class Outer {
    public class Level1 {
        public class Level2 {
            public class Level3 {
                public void deepMethod() {}
            }
        }
    }
}

// Malformed code that shouldn't crash parser
public class MissingBrace {
    public void method() {
        // Missing closing brace

// Complex generics with wildcards
public class ComplexGenerics<T extends Comparable<? super T> & Serializable> {
    public <U extends T> void wildcardMethod(
        Map<? extends U, ? super T> input,
        Function<? super T, ? extends U> mapper
    ) {}
}

// Annotation with all possible targets
@Target({ElementType.TYPE, ElementType.METHOD, ElementType.FIELD, ElementType.PARAMETER})
@Retention(RetentionPolicy.RUNTIME)
public @interface ComplexAnnotation {
    String value() default "";
    Class<?>[] types() default {};
    ElementType[] targets() default {};
    int[] numbers() default {1, 2, 3};
}

// Enum with complex features
public enum ComplexEnum implements Comparable<ComplexEnum>, Serializable {
    FIRST("first", 1) {
        @Override
        public void abstractMethod() {
            System.out.println("FIRST implementation");
        }
    },
    SECOND("second", 2) {
        @Override
        public void abstractMethod() {
            System.out.println("SECOND implementation");
        }
    };

    private final String name;
    private final int value;

    ComplexEnum(String name, int value) {
        this.name = name;
        this.value = value;
    }

    public abstract void abstractMethod();

    public String getName() { return name; }
    public int getValue() { return value; }
}

// Interface with default and static methods
public interface ModernInterface {
    void abstractMethod();

    default void defaultMethod() {
        System.out.println("Default implementation");
    }

    static void staticMethod() {
        System.out.println("Static method in interface");
    }

    private void privateMethod() {
        System.out.println("Private method in interface");
    }
}

// Class with all possible modifiers
public final strictfp class AllModifiers {
    public static final transient volatile int field = 0;

    public static synchronized native void nativeMethod();

    public final strictfp void strictMethod() {}
}

// Anonymous class usage
public class AnonymousExample {
    public void useAnonymousClass() {
        Runnable runnable = new Runnable() {
            @Override
            public void run() {
                System.out.println("Anonymous implementation");
            }
        };

        Comparator<String> comparator = new Comparator<String>() {
            @Override
            public int compare(String s1, String s2) {
                return s1.compareToIgnoreCase(s2);
            }
        };
    }
}

// Method with all parameter types
public class ParameterTypes {
    public void allParameterTypes(
        final int primitiveParam,
        String objectParam,
        int... varargs,
        @Nullable String annotatedParam,
        List<? extends Number> wildcardParam,
        Map<String, ? super Integer> complexWildcard
    ) {}
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);

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

      const emptyInterface = symbols.find(s => s.name === 'EmptyInterface');
      expect(emptyInterface).toBeDefined();
      expect(emptyInterface?.kind).toBe(SymbolKind.Interface);

      const utilityClass = symbols.find(s => s.name === 'UtilityClass');
      expect(utilityClass).toBeDefined();
      expect(utilityClass?.signature).toContain('final class UtilityClass');

      const outer = symbols.find(s => s.name === 'Outer');
      expect(outer).toBeDefined();

      const level3 = symbols.find(s => s.name === 'Level3');
      expect(level3).toBeDefined();

      const complexGenerics = symbols.find(s => s.name === 'ComplexGenerics');
      expect(complexGenerics).toBeDefined();
      expect(complexGenerics?.signature).toContain('<T extends Comparable');

      const complexAnnotation = symbols.find(s => s.name === 'ComplexAnnotation');
      expect(complexAnnotation).toBeDefined();
      expect(complexAnnotation?.signature).toContain('@interface ComplexAnnotation');

      const complexEnum = symbols.find(s => s.name === 'ComplexEnum');
      expect(complexEnum).toBeDefined();
      expect(complexEnum?.signature).toContain('enum ComplexEnum implements');

      const modernInterface = symbols.find(s => s.name === 'ModernInterface');
      expect(modernInterface).toBeDefined();
      expect(modernInterface?.kind).toBe(SymbolKind.Interface);

      const allModifiers = symbols.find(s => s.name === 'AllModifiers');
      expect(allModifiers).toBeDefined();
      expect(allModifiers?.signature).toContain('final strictfp class');

      const nativeMethod = symbols.find(s => s.name === 'nativeMethod');
      expect(nativeMethod).toBeDefined();
      expect(nativeMethod?.signature).toContain('native');

      const parameterTypes = symbols.find(s => s.name === 'ParameterTypes');
      expect(parameterTypes).toBeDefined();

      const allParameterTypesMethod = symbols.find(s => s.name === 'allParameterTypes');
      expect(allParameterTypesMethod).toBeDefined();
      expect(allParameterTypesMethod?.signature).toContain('int...');

      console.log(`☕ Edge case test: Extracted ${symbols.length} symbols from complex code`);
    });
  });

  describe('Type Inference', () => {
    it('should infer types from Java annotations', async () => {
      const javaCode = `
package com.example;

public class TypeExample {
    public String getName() {
        return "test";
    }

    public int calculate(int x, int y) {
        return x + y;
    }

    public List<String> getNames() {
        return Arrays.asList("a", "b");
    }

    private boolean isValid;
    public final String CONSTANT = "value";
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      const getName = symbols.find(s => s.name === 'getName');
      expect(getName).toBeDefined();
      expect(types.get(getName!.id)).toBe('String');

      const calculate = symbols.find(s => s.name === 'calculate');
      expect(calculate).toBeDefined();
      expect(types.get(calculate!.id)).toBe('int');

      const getNames = symbols.find(s => s.name === 'getNames');
      expect(getNames).toBeDefined();
      expect(types.get(getNames!.id)).toBe('List<String>');

      const isValid = symbols.find(s => s.name === 'isValid');
      expect(isValid).toBeDefined();
      expect(types.get(isValid!.id)).toBe('boolean');

      console.log(`☕ Type inference extracted ${types.size} types`);
    });
  });

  describe('Relationship Extraction', () => {
    it('should extract inheritance and implementation relationships', async () => {
      const javaCode = `
package com.example;

public class Dog extends Animal implements Runnable {
    @Override
    public void run() {
        // implementation
    }
}

public abstract class Animal {
    public abstract void move();
}

public interface Runnable {
    void run();
}
`;

      const result = await parserManager.parseFile('test.java', javaCode);
      const extractor = new JavaExtractor('java', 'test.java', javaCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance and implementation relationships
      expect(relationships.length).toBeGreaterThanOrEqual(1);

      console.log(`☕ Found ${relationships.length} Java relationships`);
    });
  });
});