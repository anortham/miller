import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { RubyExtractor } from '../../extractors/ruby-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('RubyExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Classes and Modules', () => {
    it('should extract classes, modules, and their members', async () => {
      const rubyCode = `
require 'active_support'
require_relative 'base_model'

module Comparable
  def <=>(other)
    # Implementation
  end

  def between?(min, max)
    self >= min && self <= max
  end
end

module Enumerable
  include Comparable

  def map(&block)
    result = []
    each { |item| result << block.call(item) }
    result
  end
end

class Person
  include Comparable
  extend Enumerable

  attr_reader :name, :age
  attr_writer :email
  attr_accessor :phone

  @@population = 0
  SPECIES = "Homo sapiens"

  def initialize(name, age = 0)
    @name = name
    @age = age
    @@population += 1
  end

  def self.population
    @@population
  end

  def adult?
    @age >= 18
  end

  private

  def secret_method
    "This is private"
  end

  protected

  def family_method
    "This is protected"
  end

  public

  def public_method
    "This is public"
  end
end

class Employee < Person
  def initialize(name, age, salary)
    super(name, age)
    @salary = salary
  end

  def annual_income
    @salary * 12
  end

  alias yearly_income annual_income
end
`;

      const result = await parserManager.parseFile('test.rb', rubyCode);
      const extractor = new RubyExtractor('ruby', 'test.rb', rubyCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Require statements
      const activeSupport = symbols.find(s => s.name === 'active_support');
      expect(activeSupport).toBeDefined();
      expect(activeSupport?.kind).toBe(SymbolKind.Import);

      const baseModel = symbols.find(s => s.name === 'base_model');
      expect(baseModel).toBeDefined();
      expect(baseModel?.signature).toContain("require_relative 'base_model'");

      // Module
      const comparable = symbols.find(s => s.name === 'Comparable');
      expect(comparable).toBeDefined();
      expect(comparable?.kind).toBe(SymbolKind.Module);

      // Module methods
      const spaceship = symbols.find(s => s.name === '<=>');
      expect(spaceship).toBeDefined();
      expect(spaceship?.kind).toBe(SymbolKind.Method);
      expect(spaceship?.parentId).toBe(comparable?.id);

      const between = symbols.find(s => s.name === 'between?');
      expect(between).toBeDefined();
      expect(between?.signature).toContain('def between?(min, max)');

      // Module with include
      const enumerable = symbols.find(s => s.name === 'Enumerable');
      expect(enumerable).toBeDefined();
      expect(enumerable?.signature).toContain('include Comparable');

      // Class
      const person = symbols.find(s => s.name === 'Person');
      expect(person).toBeDefined();
      expect(person?.kind).toBe(SymbolKind.Class);
      expect(person?.signature).toContain('include Comparable');
      expect(person?.signature).toContain('extend Enumerable');

      // Attribute accessors
      const nameReader = symbols.find(s => s.name === 'name' && s.signature?.includes('attr_reader'));
      expect(nameReader).toBeDefined();
      expect(nameReader?.kind).toBe(SymbolKind.Property);

      const emailWriter = symbols.find(s => s.name === 'email' && s.signature?.includes('attr_writer'));
      expect(emailWriter).toBeDefined();

      const phoneAccessor = symbols.find(s => s.name === 'phone' && s.signature?.includes('attr_accessor'));
      expect(phoneAccessor).toBeDefined();

      // Class variable
      const population = symbols.find(s => s.name === '@@population');
      expect(population).toBeDefined();
      expect(population?.kind).toBe(SymbolKind.Variable);
      expect(population?.signature).toContain('@@population = 0');

      // Constant
      const species = symbols.find(s => s.name === 'SPECIES');
      expect(species).toBeDefined();
      expect(species?.kind).toBe(SymbolKind.Constant);
      expect(species?.signature).toContain('SPECIES = "Homo sapiens"');

      // Constructor
      const initialize = symbols.find(s => s.name === 'initialize' && s.parentId === person?.id);
      expect(initialize).toBeDefined();
      expect(initialize?.kind).toBe(SymbolKind.Constructor);
      expect(initialize?.signature).toContain('def initialize(name, age = 0)');

      // Class method
      const populationMethod = symbols.find(s => s.name === 'population' && s.signature?.includes('self.'));
      expect(populationMethod).toBeDefined();
      expect(populationMethod?.signature).toContain('def self.population');

      // Instance method with question mark
      const adult = symbols.find(s => s.name === 'adult?');
      expect(adult).toBeDefined();
      expect(adult?.signature).toContain('def adult?');

      // Private method
      const secretMethod = symbols.find(s => s.name === 'secret_method');
      expect(secretMethod).toBeDefined();
      expect(secretMethod?.visibility).toBe('private');

      // Protected method
      const familyMethod = symbols.find(s => s.name === 'family_method');
      expect(familyMethod).toBeDefined();
      expect(familyMethod?.visibility).toBe('protected');

      // Public method
      const publicMethod = symbols.find(s => s.name === 'public_method');
      expect(publicMethod).toBeDefined();
      expect(publicMethod?.visibility).toBe('public');

      // Inheritance
      const employee = symbols.find(s => s.name === 'Employee');
      expect(employee).toBeDefined();
      expect(employee?.signature).toContain('class Employee < Person');

      // Method alias
      const annualIncome = symbols.find(s => s.name === 'annual_income');
      expect(annualIncome).toBeDefined();

      const yearlyIncome = symbols.find(s => s.name === 'yearly_income');
      expect(yearlyIncome).toBeDefined();
      expect(yearlyIncome?.signature).toContain('alias yearly_income annual_income');
    });
  });

  describe('Metaprogramming and Dynamic Methods', () => {
    it('should extract dynamically defined methods and metaprogramming constructs', async () => {
      const rubyCode = `
class DynamicClass
  # Define methods dynamically
  ['get', 'set', 'delete'].each do |action|
    define_method("#{action}_data") do |key|
      puts "#{action.capitalize}ting data for #{key}"
    end
  end

  # Class-level metaprogramming
  class << self
    def create_accessor(name)
      define_method(name) do
        instance_variable_get("@#{name}")
      end

      define_method("#{name}=") do |value|
        instance_variable_set("@#{name}", value)
      end
    end

    def inherited(subclass)
      puts "#{subclass} inherited from #{self}"
    end
  end

  # Method missing for dynamic behavior
  def method_missing(method_name, *args, &block)
    if method_name.to_s.start_with?('find_by_')
      attribute = method_name.to_s.sub('find_by_', '')
      puts "Finding by #{attribute} with value #{args.first}"
    else
      super
    end
  end

  def respond_to_missing?(method_name, include_private = false)
    method_name.to_s.start_with?('find_by_') || super
  end
end

# Singleton methods
obj = Object.new

def obj.singleton_method
  "I'm unique to this object"
end

class << obj
  def another_singleton
    "Another singleton method"
  end
end

# Module refinements
module StringExtensions
  refine String do
    def palindrome?
      self == self.reverse
    end

    def word_count
      self.split.size
    end
  end
end

class TextProcessor
  using StringExtensions

  def process(text)
    puts "Palindrome: #{text.palindrome?}"
    puts "Word count: #{text.word_count}"
  end
end

# Eval methods
class EvalExample
  def class_eval_example
    self.class.class_eval do
      def dynamic_method
        "Created with class_eval"
      end
    end
  end

  def instance_eval_example
    instance_eval do
      @dynamic_var = "Created with instance_eval"
    end
  end
end
`;

      const result = await parserManager.parseFile('test.rb', rubyCode);
      const extractor = new RubyExtractor('ruby', 'test.rb', rubyCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Dynamic class
      const dynamicClass = symbols.find(s => s.name === 'DynamicClass');
      expect(dynamicClass).toBeDefined();

      // Dynamically defined methods (should extract the define_method call)
      const defineMethodCall = symbols.find(s => s.signature?.includes('define_method'));
      expect(defineMethodCall).toBeDefined();

      // Singleton class
      const singletonClass = symbols.find(s => s.signature?.includes('class << self'));
      expect(singletonClass).toBeDefined();

      // method_missing
      const methodMissing = symbols.find(s => s.name === 'method_missing');
      expect(methodMissing).toBeDefined();
      expect(methodMissing?.signature).toContain('def method_missing(method_name, *args, &block)');

      // respond_to_missing?
      const respondToMissing = symbols.find(s => s.name === 'respond_to_missing?');
      expect(respondToMissing).toBeDefined();

      // Singleton method on object
      const singletonMethod = symbols.find(s => s.name === 'singleton_method');
      expect(singletonMethod).toBeDefined();
      expect(singletonMethod?.signature).toContain('def obj.singleton_method');

      // Module refinement
      const stringExtensions = symbols.find(s => s.name === 'StringExtensions');
      expect(stringExtensions).toBeDefined();

      // Refined method
      const palindrome = symbols.find(s => s.name === 'palindrome?');
      expect(palindrome).toBeDefined();

      // Using directive
      const textProcessor = symbols.find(s => s.name === 'TextProcessor');
      expect(textProcessor).toBeDefined();
      expect(textProcessor?.signature).toContain('using StringExtensions');

      // Eval methods
      const classEvalExample = symbols.find(s => s.name === 'class_eval_example');
      expect(classEvalExample).toBeDefined();

      const instanceEvalExample = symbols.find(s => s.name === 'instance_eval_example');
      expect(instanceEvalExample).toBeDefined();
    });
  });

  describe('Blocks, Procs, and Lambdas', () => {
    it('should extract blocks, procs, lambdas, and higher-order methods', async () => {
      const rubyCode = `
class BlockProcessor
  def initialize
    @callbacks = []
  end

  def process_with_block(&block)
    result = yield if block_given?
    puts "Block result: #{result}"
  end

  def each_item(items, &block)
    items.each do |item|
      block.call(item)
    end
  end

  def filter_items(items, &predicate)
    items.select(&predicate)
  end

  # Method that returns a proc
  def create_multiplier(factor)
    proc { |x| x * factor }
  end

  # Method that returns a lambda
  def create_validator(min, max)
    lambda { |value| value.between?(min, max) }
  end

  # Method with block parameter
  def transform_data(data, transformer: nil, &block)
    processor = transformer || block
    data.map(&processor)
  end
end

# Various ways to create procs and lambdas
doubler = proc { |x| x * 2 }
tripler = Proc.new { |x| x * 3 }
validator = lambda { |x| x > 0 }
shorthand_lambda = ->(x) { x.upcase }

# Block usage examples
numbers = [1, 2, 3, 4, 5]

# Block with each
numbers.each do |num|
  puts num * 2
end

# Block with map
squared = numbers.map { |n| n ** 2 }

# Block with select
evens = numbers.select(&:even?)

# Proc and lambda assignments
add_one = -> (x) { x + 1 }
multiply = proc { |a, b| a * b }

class EventHandler
  def initialize
    @on_success = nil
    @on_error = nil
  end

  def on_success(&block)
    @on_success = block
  end

  def on_error(&block)
    @on_error = block
  end

  def trigger_success(data)
    @on_success&.call(data)
  end

  def trigger_error(error)
    @on_error&.call(error)
  end
end

# Method that takes multiple block types
def flexible_processor(data, &block)
  case block.arity
  when 1
    data.map(&block)
  when 2
    data.each_with_index.map(&block)
  else
    data
  end
end
`;

      const result = await parserManager.parseFile('test.rb', rubyCode);
      const extractor = new RubyExtractor('ruby', 'test.rb', rubyCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Class with block methods
      const blockProcessor = symbols.find(s => s.name === 'BlockProcessor');
      expect(blockProcessor).toBeDefined();

      // Method with block parameter
      const processWithBlock = symbols.find(s => s.name === 'process_with_block');
      expect(processWithBlock).toBeDefined();
      expect(processWithBlock?.signature).toContain('(&block)');

      const eachItem = symbols.find(s => s.name === 'each_item');
      expect(eachItem).toBeDefined();
      expect(eachItem?.signature).toContain('(items, &block)');

      // Method returning proc
      const createMultiplier = symbols.find(s => s.name === 'create_multiplier');
      expect(createMultiplier).toBeDefined();

      // Method returning lambda
      const createValidator = symbols.find(s => s.name === 'create_validator');
      expect(createValidator).toBeDefined();

      // Method with named block parameter
      const transformData = symbols.find(s => s.name === 'transform_data');
      expect(transformData).toBeDefined();
      expect(transformData?.signature).toContain('transformer: nil, &block');

      // Proc assignments
      const doubler = symbols.find(s => s.name === 'doubler');
      expect(doubler).toBeDefined();
      expect(doubler?.signature).toContain('proc { |x| x * 2 }');

      const tripler = symbols.find(s => s.name === 'tripler');
      expect(tripler).toBeDefined();
      expect(tripler?.signature).toContain('Proc.new');

      // Lambda assignments
      const validator = symbols.find(s => s.name === 'validator');
      expect(validator).toBeDefined();
      expect(validator?.signature).toContain('lambda');

      const shorthandLambda = symbols.find(s => s.name === 'shorthand_lambda');
      expect(shorthandLambda).toBeDefined();
      expect(shorthandLambda?.signature).toContain('->(x)');

      // Event handler with callbacks
      const eventHandler = symbols.find(s => s.name === 'EventHandler');
      expect(eventHandler).toBeDefined();

      const onSuccess = symbols.find(s => s.name === 'on_success');
      expect(onSuccess).toBeDefined();

      const triggerSuccess = symbols.find(s => s.name === 'trigger_success');
      expect(triggerSuccess).toBeDefined();

      // Flexible processor
      const flexibleProcessor = symbols.find(s => s.name === 'flexible_processor');
      expect(flexibleProcessor).toBeDefined();
    });
  });

  describe('Constants, Symbols, and Variables', () => {
    it('should extract different types of constants, symbols, and variables', async () => {
      const rubyCode = `
module Constants
  # Various constant types
  PI = 3.14159
  APP_NAME = "My Application"
  VERSION = [1, 2, 3]

  # Nested constants
  module Database
    HOST = "localhost"
    PORT = 5432
    CONFIG = {
      host: HOST,
      port: PORT,
      adapter: "postgresql"
    }
  end

  # Class constants
  class User
    DEFAULT_ROLE = :user
    VALID_STATUSES = [:active, :inactive, :pending]
    MAX_LOGIN_ATTEMPTS = 3

    def initialize(name)
      @name = name
      @login_attempts = 0
      @@user_count ||= 0
      @@user_count += 1
    end

    def self.user_count
      @@user_count
    end
  end
end

# Symbol usage
status_symbols = [:pending, :approved, :rejected]
method_name = :calculate_total
hash_with_symbols = {
  name: "John",
  age: 30,
  active: true
}

# Different variable types
$global_variable = "I'm global"
@instance_variable = "I'm an instance variable"
@@class_variable = "I'm a class variable"
local_variable = "I'm local"
CONSTANT_VARIABLE = "I'm a constant"

class VariableExample
  def initialize
    @instance_var = "instance"
    @@class_var = "class"
  end

  def self.class_method
    @@class_var
  end

  def instance_method
    local_var = "local"
    @instance_var
  end

  # Constant within class
  INNER_CONSTANT = "inner"
end

# Parallel assignment
a, b, c = 1, 2, 3
first, *rest = [1, 2, 3, 4, 5]
x, y = y, x  # swap

# Multiple assignment with methods
def multiple_return
  return 1, 2, 3
end

one, two, three = multiple_return

# Constants with special characters
class HTTPClient
  DEFAULT_TIMEOUT = 30
  MAX_RETRIES = 3
  BASE_URL = "https://api.example.com"
end
`;

      const result = await parserManager.parseFile('test.rb', rubyCode);
      const extractor = new RubyExtractor('ruby', 'test.rb', rubyCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Module constants
      const pi = symbols.find(s => s.name === 'PI');
      expect(pi).toBeDefined();
      expect(pi?.kind).toBe(SymbolKind.Constant);
      expect(pi?.signature).toContain('PI = 3.14159');

      const appName = symbols.find(s => s.name === 'APP_NAME');
      expect(appName).toBeDefined();
      expect(appName?.signature).toContain('"My Application"');

      // Nested module
      const database = symbols.find(s => s.name === 'Database');
      expect(database).toBeDefined();
      expect(database?.kind).toBe(SymbolKind.Module);

      // Nested constants
      const host = symbols.find(s => s.name === 'HOST');
      expect(host).toBeDefined();
      expect(host?.parentId).toBe(database?.id);

      const config = symbols.find(s => s.name === 'CONFIG');
      expect(config).toBeDefined();
      expect(config?.signature).toContain('CONFIG = {');

      // Class with constants
      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();

      const defaultRole = symbols.find(s => s.name === 'DEFAULT_ROLE');
      expect(defaultRole).toBeDefined();
      expect(defaultRole?.signature).toContain(':user');

      const validStatuses = symbols.find(s => s.name === 'VALID_STATUSES');
      expect(validStatuses).toBeDefined();
      expect(validStatuses?.signature).toContain('[:active, :inactive, :pending]');

      // Instance variables
      const nameVar = symbols.find(s => s.name === '@name');
      expect(nameVar).toBeDefined();
      expect(nameVar?.kind).toBe(SymbolKind.Variable);

      // Class variables
      const userCount = symbols.find(s => s.name === '@@user_count');
      expect(userCount).toBeDefined();
      expect(userCount?.kind).toBe(SymbolKind.Variable);

      // Global variable
      const globalVar = symbols.find(s => s.name === '$global_variable');
      expect(globalVar).toBeDefined();
      expect(globalVar?.signature).toContain("$global_variable = \"I'm global\"");

      // Symbol assignments
      const statusSymbols = symbols.find(s => s.name === 'status_symbols');
      expect(statusSymbols).toBeDefined();
      expect(statusSymbols?.signature).toContain('[:pending, :approved, :rejected]');

      const methodName = symbols.find(s => s.name === 'method_name');
      expect(methodName).toBeDefined();
      expect(methodName?.signature).toContain(':calculate_total');

      // Hash with symbols
      const hashWithSymbols = symbols.find(s => s.name === 'hash_with_symbols');
      expect(hashWithSymbols).toBeDefined();
      expect(hashWithSymbols?.signature).toContain('name: "John"');

      // Parallel assignment
      const parallelA = symbols.find(s => s.name === 'a' && s.signature?.includes('a, b, c = 1, 2, 3'));
      expect(parallelA).toBeDefined();

      const rest = symbols.find(s => s.name === 'rest');
      expect(rest).toBeDefined();
      expect(rest?.signature).toContain('first, *rest = [1, 2, 3, 4, 5]');

      // Multiple return
      const multipleReturn = symbols.find(s => s.name === 'multiple_return');
      expect(multipleReturn).toBeDefined();
      expect(multipleReturn?.signature).toContain('return 1, 2, 3');
    });
  });

  describe('Mixins and Module Inclusion', () => {
    it('should extract module inclusions, extensions, and prepends', async () => {
      const rubyCode = `
module Loggable
  def log(message)
    puts "[LOG] #{message}"
  end

  def self.included(base)
    base.extend(ClassMethods)
  end

  module ClassMethods
    def log_class_info
      puts "Class: #{self.name}"
    end
  end
end

module Cacheable
  def cache_key
    "#{self.class.name.downcase}_#{id}"
  end

  def cached?
    Cache.exists?(cache_key)
  end
end

module Timestampable
  def self.prepended(base)
    base.class_eval do
      attr_accessor :created_at, :updated_at
    end
  end

  def touch
    self.updated_at = Time.now
  end
end

class BaseModel
  include Loggable
  include Cacheable
  prepend Timestampable

  attr_reader :id

  def initialize(id)
    @id = id
    @created_at = Time.now
    @updated_at = Time.now
  end
end

class User < BaseModel
  extend Forwardable

  def_delegator :@profile, :email
  def_delegator :@profile, :name, :full_name

  def initialize(id, profile)
    super(id)
    @profile = profile
  end
end

# Module as namespace
module API
  module V1
    class UsersController
      include Loggable

      def index
        log "Fetching all users"
        # Implementation
      end
    end
  end

  module V2
    class UsersController
      include Loggable

      def index
        log "Fetching all users (v2)"
        # Implementation
      end
    end
  end
end

# Multiple inclusion patterns
class ComplexModel
  include Enumerable
  include Comparable
  extend Forwardable

  def initialize(items)
    @items = items
  end

  def each(&block)
    @items.each(&block)
  end

  def <=>(other)
    @items.size <=> other.items.size
  end

  protected

  attr_reader :items
end
`;

      const result = await parserManager.parseFile('test.rb', rubyCode);
      const extractor = new RubyExtractor('ruby', 'test.rb', rubyCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Module with callback
      const loggable = symbols.find(s => s.name === 'Loggable');
      expect(loggable).toBeDefined();
      expect(loggable?.kind).toBe(SymbolKind.Module);

      const included = symbols.find(s => s.name === 'included');
      expect(included).toBeDefined();
      expect(included?.signature).toContain('def self.included(base)');

      // Nested module
      const classMethods = symbols.find(s => s.name === 'ClassMethods' && s.kind === SymbolKind.Module);
      expect(classMethods).toBeDefined();
      expect(classMethods?.parentId).toBe(loggable?.id);

      // Module with prepend callback
      const timestampable = symbols.find(s => s.name === 'Timestampable');
      expect(timestampable).toBeDefined();

      const prepended = symbols.find(s => s.name === 'prepended');
      expect(prepended).toBeDefined();
      expect(prepended?.signature).toContain('def self.prepended(base)');

      // Class with multiple inclusions
      const baseModel = symbols.find(s => s.name === 'BaseModel');
      expect(baseModel).toBeDefined();
      expect(baseModel?.signature).toContain('include Loggable');
      expect(baseModel?.signature).toContain('include Cacheable');
      expect(baseModel?.signature).toContain('prepend Timestampable');

      // Class with extension
      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.signature).toContain('extend Forwardable');

      // Delegation
      const defDelegator = symbols.find(s => s.signature?.includes('def_delegator'));
      expect(defDelegator).toBeDefined();

      // Nested namespace modules
      const apiV1 = symbols.find(s => s.name === 'V1');
      expect(apiV1).toBeDefined();

      const apiV2 = symbols.find(s => s.name === 'V2');
      expect(apiV2).toBeDefined();

      const v1Controller = symbols.find(s => s.name === 'UsersController' && s.signature?.includes('V1'));
      expect(v1Controller).toBeDefined();

      const v2Controller = symbols.find(s => s.name === 'UsersController' && s.signature?.includes('V2'));
      expect(v2Controller).toBeDefined();

      // Complex model with multiple mixins
      const complexModel = symbols.find(s => s.name === 'ComplexModel');
      expect(complexModel).toBeDefined();
      expect(complexModel?.signature).toContain('include Enumerable');
      expect(complexModel?.signature).toContain('include Comparable');
      expect(complexModel?.signature).toContain('extend Forwardable');
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer basic types from Ruby assignments and method signatures', async () => {
      const rubyCode = `
class Calculator
  def add(a, b)
    a + b
  end

  def multiply_by_two(number)
    number * 2
  end

  def get_user_name
    "John Doe"
  end

  def get_numbers
    [1, 2, 3, 4, 5]
  end

  def get_config
    { host: "localhost", port: 3000 }
  end

  def is_valid?
    true
  end

  attr_reader :result
  attr_writer :factor
  attr_accessor :mode

  def initialize
    @result = 0
    @factor = 1.0
    @mode = :automatic
  end
end
`;

      const result = await parserManager.parseFile('test.rb', rubyCode);
      const extractor = new RubyExtractor('ruby', 'test.rb', rubyCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      // In Ruby, type inference is much more limited due to dynamic nature
      // We can infer some basic types from literals and patterns

      const getUserName = symbols.find(s => s.name === 'get_user_name');
      expect(getUserName).toBeDefined();
      // Ruby type inference would be limited, but we might infer String from return value

      const getNumbers = symbols.find(s => s.name === 'get_numbers');
      expect(getNumbers).toBeDefined();
      // Might infer Array from return value

      const isValid = symbols.find(s => s.name === 'is_valid?');
      expect(isValid).toBeDefined();
      // Might infer Boolean from method name pattern and return value

      // Instance variables with initial values
      const resultSymbol = symbols.find(s => s.name === '@result');
      expect(resultSymbol).toBeDefined();
      // Might infer Integer from assignment

      const factor = symbols.find(s => s.name === '@factor');
      expect(factor).toBeDefined();
      // Might infer Float from assignment

      const mode = symbols.find(s => s.name === '@mode');
      expect(mode).toBeDefined();
      // Might infer Symbol from assignment
    });

    it('should extract inheritance and module inclusion relationships', async () => {
      const rubyCode = `
module Drawable
  def draw
    puts "Drawing"
  end
end

module Colorable
  def set_color(color)
    @color = color
  end
end

module Comparable
  def <=>(other)
    # Implementation
  end
end

class Shape
  include Drawable
  extend Colorable

  def initialize
    @color = :black
  end
end

class Circle < Shape
  include Comparable

  def initialize(radius)
    super()
    @radius = radius
  end

  def area
    Math::PI * @radius ** 2
  end

  def <=>(other)
    area <=> other.area
  end
end

class Rectangle < Shape
  def initialize(width, height)
    super()
    @width = width
    @height = height
  end

  def area
    @width * @height
  end
end
`;

      const result = await parserManager.parseFile('test.rb', rubyCode);
      const extractor = new RubyExtractor('ruby', 'test.rb', rubyCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find inheritance and module inclusion relationships
      expect(relationships.length).toBeGreaterThanOrEqual(4);

      // Circle extends Shape
      const circleShape = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Circle' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Shape'
      );
      expect(circleShape).toBeDefined();

      // Rectangle extends Shape
      const rectangleShape = relationships.find(r =>
        r.kind === 'extends' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Rectangle' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Shape'
      );
      expect(rectangleShape).toBeDefined();

      // Shape includes Drawable
      const shapeDrawable = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Shape' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Drawable'
      );
      expect(shapeDrawable).toBeDefined();

      // Circle includes Comparable
      const circleComparable = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'Circle' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Comparable'
      );
      expect(circleComparable).toBeDefined();
    });
  });
});