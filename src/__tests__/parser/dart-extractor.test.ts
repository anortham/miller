import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { DartExtractor } from '../../extractors/dart-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('DartExtractor', () => {
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

  describe('Classes and Constructors', () => {
    it('should extract classes with various constructor types', async () => {
      const dartCode = `
class Person {
  String name;
  int age;
  String? email;
  late bool isVerified;

  // Default constructor
  Person(this.name, this.age, {this.email});

  // Named constructor
  Person.baby(this.name) : age = 0;

  // Factory constructor
  factory Person.fromJson(Map<String, dynamic> json) {
    return Person(json['name'], json['age'], email: json['email']);
  }

  // Const constructor
  const Person.unknown() : name = 'Unknown', age = 0, email = null;

  void greet() {
    print('Hello, I am $name');
  }

  int get birthYear => DateTime.now().year - age;

  set newAge(int value) {
    age = value;
  }
}

abstract class Animal {
  String get sound;
  void makeSound() => print(sound);
}

class Dog extends Animal {
  @override
  String get sound => 'Woof!';

  static int totalDogs = 0;

  Dog() {
    totalDogs++;
  }
}`;

      const result = await parserManager.parseFile('test.dart', dartCode);

      const extractor = new DartExtractor('dart', 'test.dart', dartCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract classes
      const personClass = symbols.find(s => s.name === 'Person' && s.kind === SymbolKind.Class);
      expect(personClass).toBeDefined();
      expect(personClass?.signature).toContain('class Person');

      const animalClass = symbols.find(s => s.name === 'Animal' && s.kind === SymbolKind.Class);
      expect(animalClass).toBeDefined();
      expect(animalClass?.signature).toContain('abstract class Animal');

      const dogClass = symbols.find(s => s.name === 'Dog' && s.kind === SymbolKind.Class);
      expect(dogClass).toBeDefined();

      // Should extract constructors
      const constructors = symbols.filter(s => s.kind === SymbolKind.Constructor);
      expect(constructors.length).toBeGreaterThanOrEqual(4); // Default, named, factory, const

      const defaultConstructor = constructors.find(s => s.name === 'Person');
      expect(defaultConstructor).toBeDefined();

      const namedConstructor = constructors.find(s => s.name === 'Person.baby');
      expect(namedConstructor).toBeDefined();

      const factoryConstructor = constructors.find(s => s.name === 'Person.fromJson');
      expect(factoryConstructor).toBeDefined();
      expect(factoryConstructor?.signature).toContain('factory');

      // Should extract methods
      const greetMethod = symbols.find(s => s.name === 'greet');
      expect(greetMethod).toBeDefined();
      expect(greetMethod?.kind).toBe(SymbolKind.Method);

      const makeSoundMethod = symbols.find(s => s.name === 'makeSound');
      expect(makeSoundMethod).toBeDefined();

      // Should extract getters and setters
      const birthYearGetter = symbols.find(s => s.name === 'birthYear');
      expect(birthYearGetter).toBeDefined();
      expect(birthYearGetter?.signature).toContain('get');

      const newAgeSetter = symbols.find(s => s.name === 'newAge');
      expect(newAgeSetter).toBeDefined();
      expect(newAgeSetter?.signature).toContain('set');

      // Should extract fields/properties
      const nameField = symbols.find(s => s.name === 'name');
      expect(nameField).toBeDefined();
      expect(nameField?.kind).toBe(SymbolKind.Field);

      const totalDogsField = symbols.find(s => s.name === 'totalDogs');
      expect(totalDogsField).toBeDefined();
      expect(totalDogsField?.signature).toContain('static');
    });
  });

  describe('Mixins and Extensions', () => {
    it('should extract mixins and extensions', async () => {
      const dartCode = `
mixin Flyable {
  double altitude = 0;

  void fly() {
    altitude += 100;
    print('Flying at altitude $altitude');
  }

  void land() => altitude = 0;
}

mixin Swimmable on Animal {
  void swim() => print('Swimming like a \${sound.toLowerCase()}');
}

class Bird extends Animal with Flyable {
  @override
  String get sound => 'Tweet!';
}

class Duck extends Animal with Flyable, Swimmable {
  @override
  String get sound => 'Quack!';
}

extension StringExtensions on String {
  String get capitalized =>
    this.isNotEmpty ? '\${this[0].toUpperCase()}\${this.substring(1)}' : this;

  bool get isEmail => contains('@') && contains('.');

  String reverse() => split('').reversed.join('');
}

extension on List<int> {
  int get sum => fnew(0, (a, b) => a + b);
}`;

      const result = await parserManager.parseFile('test.dart', dartCode);

      const extractor = new DartExtractor('dart', 'test.dart', dartCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract mixins
      const flyableMixin = symbols.find(s => s.name === 'Flyable');
      expect(flyableMixin).toBeDefined();
      expect(flyableMixin?.signature).toContain('mixin Flyable');

      const swimmableMixin = symbols.find(s => s.name === 'Swimmable');
      expect(swimmableMixin).toBeDefined();
      expect(swimmableMixin?.signature).toContain('mixin Swimmable on Animal');

      // Should extract mixin methods
      const flyMethod = symbols.find(s => s.name === 'fly');
      expect(flyMethod).toBeDefined();

      const swimMethod = symbols.find(s => s.name === 'swim');
      expect(swimMethod).toBeDefined();

      // Should extract classes with mixins
      const birdClass = symbols.find(s => s.name === 'Bird');
      expect(birdClass).toBeDefined();
      expect(birdClass?.signature).toContain('with Flyable');

      const duckClass = symbols.find(s => s.name === 'Duck');
      expect(duckClass).toBeDefined();
      expect(duckClass?.signature).toContain('with Flyable, Swimmable');

      // Should extract extensions
      const stringExtension = symbols.find(s => s.name === 'StringExtensions');
      expect(stringExtension).toBeDefined();
      expect(stringExtension?.signature).toContain('extension StringExtensions on String');

      // Should extract extension methods
      const capitalizedGetter = symbols.find(s => s.name === 'capitalized');
      expect(capitalizedGetter).toBeDefined();

      const isEmailGetter = symbols.find(s => s.name === 'isEmail');
      expect(isEmailGetter).toBeDefined();

      const reverseMethod = symbols.find(s => s.name === 'reverse');
      expect(reverseMethod).toBeDefined();
    });
  });

  describe('Enums and Functions', () => {
    it('should extract enums and top-level functions', async () => {
      const dartCode = `
enum Color {
  red('Red'),
  green('Green'),
  blue('Blue');

  const Color(this.displayName);
  final String displayName;

  static Color fromHex(String hex) {
    switch (hex) {
      case '#FF0000': return Color.red;
      case '#00FF00': return Color.green;
      case '#0000FF': return Color.blue;
      default: throw ArgumentError('Invalid hex: $hex');
    }
  }
}

enum Status { pending, approved, rejected }

// Top-level functions
String formatName(String first, String last, {String? middle}) {
  return middle != null ? '$first $middle $last' : '$first $last';
}

Future<String> fetchUserData(int userId) async {
  await Future.delayed(Duration(seconds: 1));
  return 'User data for $userId';
}

Stream<int> countStream() async* {
  for (int i = 0; i < 10; i++) {
    yield i;
    await Future.delayed(Duration(milliseconds: 100));
  }
}

typedef StringCallback = void Function(String);
typedef NumberProcessor<T extends num> = T Function(T);

T processData<T extends Comparable<T>>(T data, T Function(T) processor) {
  return processor(data);
}`;

      const result = await parserManager.parseFile('test.dart', dartCode);

      const extractor = new DartExtractor('dart', 'test.dart', dartCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract enums
      const colorEnum = symbols.find(s => s.name === 'Color' && s.kind === SymbolKind.Enum);
      expect(colorEnum).toBeDefined();
      expect(colorEnum?.signature).toContain('enum Color');

      const statusEnum = symbols.find(s => s.name === 'Status' && s.kind === SymbolKind.Enum);
      expect(statusEnum).toBeDefined();

      // Should extract enum members
      const redMember = symbols.find(s => s.name === 'red');
      expect(redMember).toBeDefined();

      const greenMember = symbols.find(s => s.name === 'green');
      expect(greenMember).toBeDefined();

      // Should extract enum constructor and method
      const colorConstructor = symbols.find(s => s.name === 'Color' && s.kind === SymbolKind.Constructor);
      expect(colorConstructor).toBeDefined();

      const fromHexMethod = symbols.find(s => s.name === 'fromHex');
      expect(fromHexMethod).toBeDefined();
      expect(fromHexMethod?.signature).toContain('static');

      // Should extract top-level functions
      const formatNameFunction = symbols.find(s => s.name === 'formatName' && s.kind === SymbolKind.Function);
      expect(formatNameFunction).toBeDefined();
      expect(formatNameFunction?.signature).toContain('String formatName');

      const fetchUserDataFunction = symbols.find(s => s.name === 'fetchUserData');
      expect(fetchUserDataFunction).toBeDefined();
      expect(fetchUserDataFunction?.signature).toContain('Future<String>');
      expect(fetchUserDataFunction?.signature).toContain('async');

      const countStreamFunction = symbols.find(s => s.name === 'countStream');
      expect(countStreamFunction).toBeDefined();
      expect(countStreamFunction?.signature).toContain('Stream<int>');

      // Should extract generic function
      const processDataFunction = symbols.find(s => s.name === 'processData');
      expect(processDataFunction).toBeDefined();
      expect(processDataFunction?.signature).toContain('<T extends Comparable<T>>');

      // Should extract typedefs
      const stringCallbackTypedef = symbols.find(s => s.name === 'StringCallback');
      expect(stringCallbackTypedef).toBeDefined();
      expect(stringCallbackTypedef?.signature).toContain('typedef');

      const numberProcessorTypedef = symbols.find(s => s.name === 'NumberProcessor');
      expect(numberProcessorTypedef).toBeDefined();
      expect(numberProcessorTypedef?.signature).toContain('typedef');
      expect(numberProcessorTypedef?.signature).toContain('<T extends num>');
    });
  });

  describe('Flutter Widget Patterns', () => {
    it('should extract Flutter widget classes and methods', async () => {
      const dartCode = `
import 'package:flutter/material.dart';

class MyHomePage extends StatefulWidget {
  final String title;

  const MyHomePage({Key? key, required this.title}) : super(key: key);

  @override
  State<MyHomePage> createState() => _MyHomePageState();
}

class _MyHomePageState extends State<MyHomePage> with TickerProviderStateMixin {
  int _counter = 0;
  late AnimationController _controller;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(vsync: this, duration: Duration(seconds: 1));
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _incrementCounter() {
    setState(() {
      _counter++;
    });
  }

  @override
  Widget build(BuildContext context) {
    return Scaffnew(
      appBar: AppBar(title: Text(widget.title)),
      body: Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Text('You have pushed the button this many times:'),
            Text('$_counter', style: Theme.of(context).textTheme.headlineMedium),
          ],
        ),
      ),
      floatingActionButton: FloatingActionButton(
        onPressed: _incrementCounter,
        tooltip: 'Increment',
        child: Icon(Icons.add),
      ),
    );
  }
}

class CustomButton extends StatelessWidget {
  final VoidCallback? onPressed;
  final String text;
  final Color? color;

  const CustomButton({
    Key? key,
    this.onPressed,
    required this.text,
    this.color,
  }) : super(key: key);

  @override
  Widget build(BuildContext context) => ElevatedButton(
    onPressed: onPressed,
    style: ElevatedButton.styleFrom(backgroundColor: color),
    child: Text(text),
  );
}`;

      const result = await parserManager.parseFile('test.dart', dartCode);

      const extractor = new DartExtractor('dart', 'test.dart', dartCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract Flutter widget classes
      const myHomePageClass = symbols.find(s => s.name === 'MyHomePage');
      expect(myHomePageClass).toBeDefined();
      expect(myHomePageClass?.signature).toContain('extends StatefulWidget');

      const stateClass = symbols.find(s => s.name === '_MyHomePageState');
      expect(stateClass).toBeDefined();
      expect(stateClass?.signature).toContain('extends State<MyHomePage>');
      expect(stateClass?.signature).toContain('with TickerProviderStateMixin');

      const customButtonClass = symbols.find(s => s.name === 'CustomButton');
      expect(customButtonClass).toBeDefined();
      expect(customButtonClass?.signature).toContain('extends StatelessWidget');

      // Should extract lifecycle methods
      const initStateMethod = symbols.find(s => s.name === 'initState');
      expect(initStateMethod).toBeDefined();
      expect(initStateMethod?.signature).toContain('@override');

      const disposeMethod = symbols.find(s => s.name === 'dispose');
      expect(disposeMethod).toBeDefined();

      // Should extract build methods
      const buildMethods = symbols.filter(s => s.name === 'build');
      expect(buildMethods.length).toBe(2); // One for each widget

      const homePageBuild = buildMethods.find(s => s.signature?.includes('Widget build'));
      expect(homePageBuild).toBeDefined();

      // Should extract custom methods
      const incrementMethod = symbols.find(s => s.name === '_incrementCounter');
      expect(incrementMethod).toBeDefined();
      expect(incrementMethod?.visibility).toBe('private');

      // Should extract createState method
      const createStateMethod = symbols.find(s => s.name === 'createState');
      expect(createStateMethod).toBeDefined();
      expect(createStateMethod?.signature).toContain('@override');

      // Should extract fields
      const titleField = symbols.find(s => s.name === 'title');
      expect(titleField).toBeDefined();
      expect(titleField?.signature).toContain('final String title');

      const counterField = symbols.find(s => s.name === '_counter');
      expect(counterField).toBeDefined();
      expect(counterField?.visibility).toBe('private');

      const controllerField = symbols.find(s => s.name === '_controller');
      expect(controllerField).toBeDefined();
      expect(controllerField?.signature).toContain('late AnimationController');
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer types and extract relationships', async () => {
      const dartCode = `
abstract class Shape {
  double get area;
  String describe() => 'A shape with area \${area}';
}

class Rectangle extends Shape {
  final double width;
  final double height;

  Rectangle(this.width, this.height);

  @override
  double get area => width * height;
}

class Circle extends Shape {
  final double radius;

  Circle(this.radius);

  @override
  double get area => 3.14159 * radius * radius;
}

mixin ColoredMixin {
  Color? color;
  void setColor(Color newColor) => color = newColor;
}

class ColoredRectangle extends Rectangle with ColoredMixin {
  ColoredRectangle(double width, double height) : super(width, height);
}

// Generic class
class Container<T> {
  late T _value;

  Container(this._value);

  T get value => _value;
  set value(T newValue) => _value = newValue;

  void process<R>(R Function(T) processor) {
    // Process the value
  }
}`;

      const result = await parserManager.parseFile('test.dart', dartCode);

      const extractor = new DartExtractor('dart', 'test.dart', dartCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);
      const types = extractor.inferTypes(symbols);

      // Should extract inheritance relationships
      expect(relationships.length).toBeGreaterThan(0);

      const rectangleInheritance = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Rectangle'
      );
      expect(rectangleInheritance).toBeDefined();

      const circleInheritance = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Circle'
      );
      expect(circleInheritance).toBeDefined();

      // Should extract mixin relationships
      const mixinRelationship = relationships.find(r =>
        r.kind === 'with' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'ColoredRectangle'
      );
      expect(mixinRelationship).toBeDefined();

      // Should infer types
      expect(types.size).toBeGreaterThan(0);

      // Should identify generic types
      const containerClass = symbols.find(s => s.name === 'Container');
      expect(containerClass).toBeDefined();
      expect(containerClass?.signature).toContain('<T>');

      const processMethod = symbols.find(s => s.name === 'process');
      expect(processMethod).toBeDefined();
      expect(processMethod?.signature).toContain('<R>');

      // Should handle getter/setter pairs
      const valueGetter = symbols.find(s => s.name === 'value' && s.signature?.includes('get'));
      expect(valueGetter).toBeDefined();

      const valueSetter = symbols.find(s => s.name === 'value' && s.signature?.includes('set'));
      expect(valueSetter).toBeDefined();
    });
  });
});