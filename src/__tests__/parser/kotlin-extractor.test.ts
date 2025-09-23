import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { KotlinExtractor } from '../../extractors/kotlin-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('KotlinExtractor', () => {
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

  describe('Classes and Data Classes', () => {
    it('should extract classes, data classes, and their members', async () => {
      const kotlinCode = `
package com.example.models

import kotlinx.serialization.Serializable

class Vehicle(
    val brand: String,
    private var speed: Int = 0
) {
    fun accelerate() {
        speed += 10
    }

    fun getSpeed(): Int = speed

    companion object {
        const val MAX_SPEED = 200

        fun createDefault(): Vehicle {
            return Vehicle("Unknown")
        }
    }
}

data class Point(val x: Double, val y: Double) {
    fun distanceFromOrigin(): Double {
        return kotlin.math.sqrt(x * x + y * y)
    }
}

@Serializable
data class User(
    val id: Long,
    val name: String,
    val email: String?,
    val isActive: Boolean = true
)

abstract class Shape {
    abstract val area: Double
    abstract fun draw()

    open fun describe(): String {
        return "A shape with area $area"
    }
}

class Circle(private val radius: Double) : Shape() {
    override val area: Double
        get() = kotlin.math.PI * radius * radius

    override fun draw() {
        println("Drawing circle with radius $radius")
    }
}
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Package declaration
      const packageSymbol = symbols.find(s => s.name === 'com.example.models');
      expect(packageSymbol).toBeDefined();
      expect(packageSymbol?.kind).toBe(SymbolKind.Namespace);

      // Import declaration
      const importSymbol = symbols.find(s => s.name === 'kotlinx.serialization.Serializable');
      expect(importSymbol).toBeDefined();
      expect(importSymbol?.kind).toBe(SymbolKind.Import);

      // Regular class with primary constructor
      const vehicle = symbols.find(s => s.name === 'Vehicle');
      expect(vehicle).toBeDefined();
      expect(vehicle?.kind).toBe(SymbolKind.Class);
      expect(vehicle?.signature).toContain('class Vehicle');

      // Primary constructor parameters as properties
      const brand = symbols.find(s => s.name === 'brand');
      expect(brand).toBeDefined();
      expect(brand?.kind).toBe(SymbolKind.Property);
      expect(brand?.signature).toContain('val brand: String');

      const speed = symbols.find(s => s.name === 'speed' && s.parentId === vehicle?.id);
      expect(speed).toBeDefined();
      expect(speed?.visibility).toBe('private');
      expect(speed?.signature).toContain('var speed: Int = 0');

      // Methods
      const accelerate = symbols.find(s => s.name === 'accelerate');
      expect(accelerate).toBeDefined();
      expect(accelerate?.kind).toBe(SymbolKind.Method);

      // Expression body function
      const getSpeed = symbols.find(s => s.name === 'getSpeed');
      expect(getSpeed).toBeDefined();
      expect(getSpeed?.signature).toContain('fun getSpeed(): Int = speed');

      // Companion object
      const companion = symbols.find(s => s.name === 'Companion');
      expect(companion).toBeDefined();
      expect(companion?.signature).toContain('companion object');

      // Const val
      const maxSpeed = symbols.find(s => s.name === 'MAX_SPEED');
      expect(maxSpeed).toBeDefined();
      expect(maxSpeed?.kind).toBe(SymbolKind.Constant);
      expect(maxSpeed?.signature).toContain('const val MAX_SPEED = 200');

      // Data class
      const point = symbols.find(s => s.name === 'Point');
      expect(point).toBeDefined();
      expect(point?.signature).toContain('data class Point');

      // Annotation on data class
      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.signature).toContain('@Serializable');

      // Nullable type
      const email = symbols.find(s => s.name === 'email');
      expect(email).toBeDefined();
      expect(email?.signature).toContain('String?');

      // Default parameter
      const isActive = symbols.find(s => s.name === 'isActive');
      expect(isActive).toBeDefined();
      expect(isActive?.signature).toContain('= true');

      // Abstract class
      const shape = symbols.find(s => s.name === 'Shape');
      expect(shape).toBeDefined();
      expect(shape?.signature).toContain('abstract class Shape');

      // Abstract property
      const area = symbols.find(s => s.name === 'area' && s.parentId === shape?.id);
      expect(area).toBeDefined();
      expect(area?.signature).toContain('abstract val area: Double');

      // Override in subclass
      const circle = symbols.find(s => s.name === 'Circle');
      expect(circle).toBeDefined();
      expect(circle?.signature).toContain('Circle(private val radius: Double) : Shape()');

      const circleArea = symbols.find(s => s.name === 'area' && s.parentId === circle?.id);
      expect(circleArea).toBeDefined();
      expect(circleArea?.signature).toContain('override val area: Double');
    });
  });

  describe('Objects and Sealed Classes', () => {
    it('should extract object declarations and sealed class hierarchies', async () => {
      const kotlinCode = `
object DatabaseConfig {
    const val URL = "jdbc:postgresql://localhost:5432/mydb"
    const val DRIVER = "org.postgresql.Driver"

    fun getConnection(): Connection {
        return DriverManager.getConnection(URL)
    }
}

object Utils : Serializable {
    fun formatDate(date: Date): String {
        return SimpleDateFormat("yyyy-MM-dd").format(date)
    }
}

sealed class Result<out T> {
    object Loading : Result<Nothing>()

    data class Success<T>(val data: T) : Result<T>()

    data class Error(
        val exception: Throwable,
        val message: String = exception.message ?: "Unknown error"
    ) : Result<Nothing>()
}

sealed interface Command {
    object Start : Command
    object Stop : Command
    data class Configure(val settings: Map<String, Any>) : Command
}

enum class Direction {
    NORTH, SOUTH, EAST, WEST;

    fun opposite(): Direction = when (this) {
        NORTH -> SOUTH
        SOUTH -> NORTH
        EAST -> WEST
        WEST -> EAST
    }
}

enum class Color(val rgb: Int) {
    RED(0xFF0000),
    GREEN(0x00FF00),
    BLUE(0x0000FF);

    companion object {
        fun fromRgb(rgb: Int): Color? {
            return values().find { it.rgb == rgb }
        }
    }
}
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Object declaration
      const databaseConfig = symbols.find(s => s.name === 'DatabaseConfig');
      expect(databaseConfig).toBeDefined();
      expect(databaseConfig?.signature).toContain('object DatabaseConfig');

      // Object with inheritance
      const utils = symbols.find(s => s.name === 'Utils');
      expect(utils).toBeDefined();
      expect(utils?.signature).toContain('object Utils : Serializable');

      // Sealed class
      const resultSymbol = symbols.find(s => s.name === 'Result');
      expect(resultSymbol).toBeDefined();
      expect(resultSymbol?.signature).toContain('sealed class Result<out T>');

      // Object inside sealed class
      const loading = symbols.find(s => s.name === 'Loading');
      expect(loading).toBeDefined();
      expect(loading?.signature).toContain('object Loading : Result<Nothing>()');

      // Data class extending sealed class
      const success = symbols.find(s => s.name === 'Success');
      expect(success).toBeDefined();
      expect(success?.signature).toContain('data class Success<T>(val data: T) : Result<T>()');

      // Sealed interface
      const command = symbols.find(s => s.name === 'Command');
      expect(command).toBeDefined();
      expect(command?.signature).toContain('sealed interface Command');

      // Simple enum
      const direction = symbols.find(s => s.name === 'Direction');
      expect(direction).toBeDefined();
      expect(direction?.kind).toBe(SymbolKind.Enum);

      const north = symbols.find(s => s.name === 'NORTH');
      expect(north).toBeDefined();
      expect(north?.kind).toBe(SymbolKind.EnumMember);

      // Enum with constructor
      const color = symbols.find(s => s.name === 'Color');
      expect(color).toBeDefined();
      expect(color?.signature).toContain('enum class Color(val rgb: Int)');

      const red = symbols.find(s => s.name === 'RED');
      expect(red).toBeDefined();
      expect(red?.signature).toContain('RED(0xFF0000)');

      // Companion object in enum
      const colorCompanion = symbols.find(s => s.name === 'Companion' && s.parentId === color?.id);
      expect(colorCompanion).toBeDefined();
    });
  });

  describe('Functions and Extension Functions', () => {
    it('should extract functions, extension functions, and higher-order functions', async () => {
      const kotlinCode = `
fun greet(name: String): String {
    return "Hello, $name!"
}

fun calculateSum(vararg numbers: Int): Int = numbers.sum()

inline fun <reified T> Any?.isInstanceOf(): Boolean {
    return this is T
}

suspend fun fetchData(url: String): String {
    delay(1000)
    return "Data from $url"
}

fun String.isValidEmail(): Boolean {
    return this.contains("@") && this.contains(".")
}

fun List<String>.joinWithCommas(): String = this.joinToString(", ")

fun <T> Collection<T>.safeGet(index: Int): T? {
    return if (index in 0 until size) elementAtOrNull(index) else null
}

fun processData(
    data: List<String>,
    filter: (String) -> Boolean,
    transform: (String) -> String
): List<String> {
    return data.filter(filter).map(transform)
}

fun createProcessor(): (String) -> String {
    return { input -> input.uppercase() }
}

tailrec fun factorial(n: Long, accumulator: Long = 1): Long {
    return if (n <= 1) accumulator else factorial(n - 1, n * accumulator)
}

infix fun String.shouldContain(substring: String): Boolean {
    return this.contains(substring)
}

operator fun Point.plus(other: Point): Point {
    return Point(x + other.x, y + other.y)
}
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Regular function
      const greet = symbols.find(s => s.name === 'greet');
      expect(greet).toBeDefined();
      expect(greet?.kind).toBe(SymbolKind.Function);
      expect(greet?.signature).toContain('fun greet(name: String): String');

      // Vararg function with expression body
      const calculateSum = symbols.find(s => s.name === 'calculateSum');
      expect(calculateSum).toBeDefined();
      expect(calculateSum?.signature).toContain('vararg numbers: Int');
      expect(calculateSum?.signature).toContain('= numbers.sum()');

      // Inline reified function
      const isInstanceOf = symbols.find(s => s.name === 'isInstanceOf');
      expect(isInstanceOf).toBeDefined();
      expect(isInstanceOf?.signature).toContain('inline fun <reified T>');

      // Suspend function
      const fetchData = symbols.find(s => s.name === 'fetchData');
      expect(fetchData).toBeDefined();
      expect(fetchData?.signature).toContain('suspend fun fetchData');

      // Extension function on String
      const isValidEmail = symbols.find(s => s.name === 'isValidEmail');
      expect(isValidEmail).toBeDefined();
      expect(isValidEmail?.signature).toContain('fun String.isValidEmail()');

      // Extension function on generic type
      const joinWithCommas = symbols.find(s => s.name === 'joinWithCommas');
      expect(joinWithCommas).toBeDefined();
      expect(joinWithCommas?.signature).toContain('fun List<String>.joinWithCommas()');

      // Generic extension function
      const safeGet = symbols.find(s => s.name === 'safeGet');
      expect(safeGet).toBeDefined();
      expect(safeGet?.signature).toContain('fun <T> Collection<T>.safeGet');

      // Higher-order function
      const processData = symbols.find(s => s.name === 'processData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('filter: (String) -> Boolean');
      expect(processData?.signature).toContain('transform: (String) -> String');

      // Function returning function
      const createProcessor = symbols.find(s => s.name === 'createProcessor');
      expect(createProcessor).toBeDefined();
      expect(createProcessor?.signature).toContain('(): (String) -> String');

      // Tailrec function
      const factorial = symbols.find(s => s.name === 'factorial');
      expect(factorial).toBeDefined();
      expect(factorial?.signature).toContain('tailrec fun factorial');

      // Infix function
      const shouldContain = symbols.find(s => s.name === 'shouldContain');
      expect(shouldContain).toBeDefined();
      expect(shouldContain?.signature).toContain('infix fun String.shouldContain');

      // Operator function
      const plus = symbols.find(s => s.name === 'plus');
      expect(plus).toBeDefined();
      expect(plus?.kind).toBe(SymbolKind.Operator);
      expect(plus?.signature).toContain('operator fun Point.plus');
    });
  });

  describe('Interfaces and Delegation', () => {
    it('should extract interfaces, delegation, and property delegation', async () => {
      const kotlinCode = `
interface Drawable {
    val color: String
    fun draw()

    fun describe(): String {
        return "Drawing with color $color"
    }
}

interface Clickable {
    fun click() {
        println("Clicked")
    }

    fun showOff() = println("I'm clickable!")
}

class Button(
    private val drawable: Drawable,
    private val clickable: Clickable
) : Drawable by drawable, Clickable by clickable {

    override fun click() {
        println("Button clicked")
        clickable.click()
    }
}

class LazyInitializer {
    val expensiveValue: String by lazy {
        println("Computing expensive value")
        "Expensive computation result"
    }

    var observableProperty: String by Delegates.observable("initial") { prop, new, new ->
        println("Property changed from $new to $new")
    }

    val notNullProperty: String by Delegates.notNull()
}

fun interface StringProcessor {
    fun process(input: String): String
}

fun interface Predicate<T> {
    fun test(item: T): Boolean
}

class ProcessorImpl : StringProcessor {
    override fun process(input: String): String {
        return input.lowercase()
    }
}
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Interface
      const drawable = symbols.find(s => s.name === 'Drawable');
      expect(drawable).toBeDefined();
      expect(drawable?.kind).toBe(SymbolKind.Interface);

      // Abstract property in interface
      const color = symbols.find(s => s.name === 'color' && s.parentId === drawable?.id);
      expect(color).toBeDefined();
      expect(color?.signature).toContain('val color: String');

      // Abstract method in interface
      const draw = symbols.find(s => s.name === 'draw' && s.parentId === drawable?.id);
      expect(draw).toBeDefined();
      expect(draw?.kind).toBe(SymbolKind.Method);

      // Method with default implementation
      const describe = symbols.find(s => s.name === 'describe' && s.parentId === drawable?.id);
      expect(describe).toBeDefined();
      expect(describe?.signature).toContain('fun describe(): String');

      // Class with delegation
      const button = symbols.find(s => s.name === 'Button');
      expect(button).toBeDefined();
      expect(button?.signature).toContain('Drawable by drawable, Clickable by clickable');

      // Lazy delegation
      const expensiveValue = symbols.find(s => s.name === 'expensiveValue');
      expect(expensiveValue).toBeDefined();
      expect(expensiveValue?.signature).toContain('by lazy');

      // Observable delegation
      const observableProperty = symbols.find(s => s.name === 'observableProperty');
      expect(observableProperty).toBeDefined();
      expect(observableProperty?.signature).toContain('by Delegates.observable');

      // NotNull delegation
      const notNullProperty = symbols.find(s => s.name === 'notNullProperty');
      expect(notNullProperty).toBeDefined();
      expect(notNullProperty?.signature).toContain('by Delegates.notNull()');

      // Fun interface (SAM interface)
      const stringProcessor = symbols.find(s => s.name === 'StringProcessor');
      expect(stringProcessor).toBeDefined();
      expect(stringProcessor?.signature).toContain('fun interface StringProcessor');

      // Generic fun interface
      const predicate = symbols.find(s => s.name === 'Predicate');
      expect(predicate).toBeDefined();
      expect(predicate?.signature).toContain('fun interface Predicate<T>');
    });
  });

  describe('Annotations and Type Aliases', () => {
    it('should extract annotations, type aliases, and metadata', async () => {
      const kotlinCode = `
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION)
@Retention(AnnotationRetention.RUNTIME)
annotation class MyAnnotation(
    val value: String,
    val priority: Int = 0
)

@Repeatable
@Target(AnnotationTarget.PROPERTY)
annotation class Author(val name: String)

typealias StringProcessor = (String) -> String
typealias UserMap = Map<String, User>
typealias Handler<T> = suspend (T) -> Unit

class ProcessingService {
    @MyAnnotation("Important service", priority = 1)
    @Author("John Doe")
    @Author("Jane Smith")
    fun processData(
        @MyAnnotation("Input parameter") input: String
    ): String {
        return input.uppercase()
    }

    @JvmStatic
    @JvmOverloads
    fun createDefault(name: String = "default"): ProcessingService {
        return ProcessingService()
    }
}

@JvmInline
value class UserId(val value: Long)

@JvmInline
value class Email(val address: String) {
    init {
        require(address.contains("@")) { "Invalid email format" }
    }
}

@file:JvmName("UtilityFunctions")
@file:JvmMultifileClass

package com.example.utils

import kotlin.jvm.JvmName
import kotlin.jvm.JvmStatic
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Annotation class
      const myAnnotation = symbols.find(s => s.name === 'MyAnnotation');
      expect(myAnnotation).toBeDefined();
      expect(myAnnotation?.signature).toContain('annotation class MyAnnotation');
      expect(myAnnotation?.signature).toContain('@Target');
      expect(myAnnotation?.signature).toContain('@Retention');

      // Repeatable annotation
      const author = symbols.find(s => s.name === 'Author');
      expect(author).toBeDefined();
      expect(author?.signature).toContain('@Repeatable');

      // Type aliases
      const stringProcessor = symbols.find(s => s.name === 'StringProcessor' && s.kind === SymbolKind.Type);
      expect(stringProcessor).toBeDefined();
      expect(stringProcessor?.signature).toContain('typealias StringProcessor = (String) -> String');

      const userMap = symbols.find(s => s.name === 'UserMap');
      expect(userMap).toBeDefined();
      expect(userMap?.signature).toContain('typealias UserMap = Map<String, User>');

      const handler = symbols.find(s => s.name === 'Handler');
      expect(handler).toBeDefined();
      expect(handler?.signature).toContain('typealias Handler<T> = suspend (T) -> Unit');

      // Method with multiple annotations
      const processData = symbols.find(s => s.name === 'processData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('@MyAnnotation');
      expect(processData?.signature).toContain('@Author("John Doe")');
      expect(processData?.signature).toContain('@Author("Jane Smith")');

      // JVM annotations
      const createDefault = symbols.find(s => s.name === 'createDefault');
      expect(createDefault).toBeDefined();
      expect(createDefault?.signature).toContain('@JvmStatic');
      expect(createDefault?.signature).toContain('@JvmOverloads');

      // Inline value class
      const userId = symbols.find(s => s.name === 'UserId');
      expect(userId).toBeDefined();
      expect(userId?.signature).toContain('@JvmInline');
      expect(userId?.signature).toContain('value class UserId');

      // Value class with validation
      const email = symbols.find(s => s.name === 'Email');
      expect(email).toBeDefined();
      expect(email?.signature).toContain('value class Email');
    });
  });

  describe('Generics and Variance', () => {
    it('should extract generic types with variance annotations', async () => {
      const kotlinCode = `
interface Producer<out T> {
    fun produce(): T
}

interface Consumer<in T> {
    fun consume(item: T)
}

interface Processor<T> {
    fun process(input: T): T
}

class Box<T>(private var item: T) {
    fun get(): T = item
    fun set(newItem: T) {
        item = newItem
    }
}

class ContravariantBox<in T> {
    fun put(item: T) {
        // Implementation
    }
}

fun <T : Comparable<T>> findMax(items: List<T>): T? {
    return items.maxOrNull()
}

fun <T> copyWhenGreater(list: List<T>, threshnew: T): List<T>
    where T : Comparable<T>, T : Number {
    return list.filter { it > threshnew }
}

inline fun <reified T> createArray(size: Int): Array<T?> {
    return arrayOfNulls<T>(size)
}

class Repository<T : Any> {
    private val items = mutableListOf<T>()

    fun add(item: T) {
        items.add(item)
    }

    inline fun <reified R : T> findByType(): List<R> {
        return items.filterIsInstance<R>()
    }
}

fun <K, V> Map<K, V>.getValueOrDefault(key: K, default: () -> V): V {
    return this[key] ?: default()
}
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Covariant interface
      const producer = symbols.find(s => s.name === 'Producer');
      expect(producer).toBeDefined();
      expect(producer?.signature).toContain('interface Producer<out T>');

      // Contravariant interface
      const consumer = symbols.find(s => s.name === 'Consumer');
      expect(consumer).toBeDefined();
      expect(consumer?.signature).toContain('interface Consumer<in T>');

      // Invariant generic class
      const box = symbols.find(s => s.name === 'Box');
      expect(box).toBeDefined();
      expect(box?.signature).toContain('class Box<T>');

      // Function with type bounds
      const findMax = symbols.find(s => s.name === 'findMax');
      expect(findMax).toBeDefined();
      expect(findMax?.signature).toContain('<T : Comparable<T>>');

      // Function with multiple type constraints
      const copyWhenGreater = symbols.find(s => s.name === 'copyWhenGreater');
      expect(copyWhenGreater).toBeDefined();
      expect(copyWhenGreater?.signature).toContain('where T : Comparable<T>, T : Number');

      // Reified generic function
      const createArray = symbols.find(s => s.name === 'createArray');
      expect(createArray).toBeDefined();
      expect(createArray?.signature).toContain('inline fun <reified T>');

      // Generic class with bounds
      const repository = symbols.find(s => s.name === 'Repository');
      expect(repository).toBeDefined();
      expect(repository?.signature).toContain('class Repository<T : Any>');

      // Extension function on generic type
      const getValueOrDefault = symbols.find(s => s.name === 'getValueOrDefault');
      expect(getValueOrDefault).toBeDefined();
      expect(getValueOrDefault?.signature).toContain('fun <K, V> Map<K, V>.getValueOrDefault');
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer types from Kotlin declarations', async () => {
      const kotlinCode = `
class DataService {
    fun fetchUsers(): List<User> {
        return emptyList()
    }

    suspend fun fetchUserById(id: Long): User? {
        return null
    }

    val cache: MutableMap<String, Any> = mutableMapOf()
    var isEnabled: Boolean = true
}

interface Repository<T> {
    suspend fun findAll(): List<T>
    suspend fun findById(id: Long): T?
}

class UserRepository : Repository<User> {
    override suspend fun findAll(): List<User> {
        return emptyList()
    }

    override suspend fun findById(id: Long): User? {
        return null
    }
}
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      // Function return types
      const fetchUsers = symbols.find(s => s.name === 'fetchUsers');
      expect(fetchUsers).toBeDefined();
      expect(types.get(fetchUsers!.id)).toBe('List<User>');

      const fetchUserById = symbols.find(s => s.name === 'fetchUserById');
      expect(fetchUserById).toBeDefined();
      expect(types.get(fetchUserById!.id)).toBe('User?');

      // Property types
      const cache = symbols.find(s => s.name === 'cache');
      expect(cache).toBeDefined();
      expect(types.get(cache!.id)).toBe('MutableMap<String, Any>');

      const isEnabled = symbols.find(s => s.name === 'isEnabled');
      expect(isEnabled).toBeDefined();
      expect(types.get(isEnabled!.id)).toBe('Boolean');
    });

    it('should extract inheritance and interface implementation relationships', async () => {
      const kotlinCode = `
interface Drawable {
    fun draw()
}

interface Clickable {
    fun click()
}

abstract class Widget : Drawable {
    abstract val size: Int
}

class Button : Widget(), Clickable {
    override val size: Int = 100

    override fun draw() {
        println("Drawing button")
    }

    override fun click() {
        println("Button clicked")
    }
}

sealed class State {
    object Loading : State()
    data class Success(val data: String) : State()
    data class Error(val message: String) : State()
}
`;

      const result = await parserManager.parseFile('test.kt', kotlinCode);
      const extractor = new KotlinExtractor('kotlin', 'test.kt', kotlinCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance and interface implementation relationships
      expect(relationships.length).toBeGreaterThanOrEqual(4);

      // Widget implements Drawable
      const widgetDrawable = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Widget' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Drawable'
      );
      expect(widgetDrawable).toBeDefined();

      // Button extends Widget
      const buttonWidget = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Button' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Widget'
      );
      expect(buttonWidget).toBeDefined();

      // Button implements Clickable
      const buttonClickable = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Button' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Clickable'
      );
      expect(buttonClickable).toBeDefined();

      // Success extends State
      const successState = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Success' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'State'
      );
      expect(successState).toBeDefined();
    });
  });
});