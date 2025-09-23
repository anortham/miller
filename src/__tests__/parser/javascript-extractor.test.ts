import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { JavaScriptExtractor } from '../../extractors/javascript-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('JavaScriptExtractor', () => {
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

  describe('Modern JavaScript Features', () => {
    it('should extract ES6+ functions, classes, and modules', async () => {
      const jsCode = `
// ES6 Imports/Exports
import { debounce, throttle } from 'lodash';
import React, { useState, useEffect } from 'react';
export { default as Component } from './Component';
export const API_URL = 'https://api.example.com';

// Arrow functions
const add = (a, b) => a + b;
const multiply = (x, y) => {
  return x * y;
};

// Async/await functions
async function fetchData(url) {
  try {
    const response = await fetch(url);
    return await response.json();
  } catch (error) {
    console.error('Fetch failed:', error);
    throw error;
  }
}

const asyncArrow = async (id) => {
  const data = await fetchData(\`/api/users/\${id}\`);
  return data;
};

// Generator functions
function* fibonacci() {
  let [a, b] = [0, 1];
  while (true) {
    yield a;
    [a, b] = [b, a + b];
  }
}

const generatorArrow = function* (items) {
  for (const item of items) {
    yield item.toUpperCase();
  }
};

// Classes with modern features
class EventEmitter {
  #listeners = new Map(); // Private field

  constructor(options = {}) {
    this.maxListeners = options.maxListeners || 10;
  }

  // Static method
  static create(options) {
    return new EventEmitter(options);
  }

  // Getter/setter
  get listenerCount() {
    return this.#listeners.size;
  }

  set maxListeners(value) {
    this._maxListeners = Math.max(0, value);
  }

  // Async method
  async emit(event, ...args) {
    const handlers = this.#listeners.get(event) || [];
    await Promise.all(handlers.map(handler => handler(...args)));
  }

  // Private method
  #validateEvent(event) {
    if (typeof event !== 'string') {
      throw new TypeError('Event must be a string');
    }
  }
}

// Class inheritance
class AsyncEventEmitter extends EventEmitter {
  constructor(options) {
    super(options);
    this.queue = [];
  }

  async processQueue() {
    while (this.queue.length > 0) {
      const event = this.queue.shift();
      await super.emit(event.name, ...event.args);
    }
  }
}
`;

      const result = await parserManager.parseFile('test.js', jsCode);
      const extractor = new JavaScriptExtractor('javascript', 'test.js', jsCode);
      const symbols = extractor.extractSymbols(result.tree);

      // ES6 Imports
      const lodashImport = symbols.find(s => s.name === 'debounce' && s.kind === SymbolKind.Import);
      expect(lodashImport).toBeDefined();
      expect(lodashImport?.signature).toContain('import { debounce, throttle } from \'lodash\'');

      const reactImport = symbols.find(s => s.name === 'React' && s.kind === SymbolKind.Import);
      expect(reactImport).toBeDefined();
      expect(reactImport?.signature).toContain('import React, { useState, useEffect } from \'react\'');

      // ES6 Exports
      const componentExport = symbols.find(s => s.name === 'Component' && s.kind === SymbolKind.Export);
      expect(componentExport).toBeDefined();
      expect(componentExport?.signature).toContain('export { default as Component }');

      const apiUrlExport = symbols.find(s => s.name === 'API_URL' && s.kind === SymbolKind.Export);
      expect(apiUrlExport).toBeDefined();
      expect(apiUrlExport?.signature).toContain('export const API_URL');

      // Arrow functions
      const addArrow = symbols.find(s => s.name === 'add');
      expect(addArrow).toBeDefined();
      expect(addArrow?.kind).toBe(SymbolKind.Function);
      expect(addArrow?.signature).toContain('const add = (a, b) => a + b');

      const multiplyArrow = symbols.find(s => s.name === 'multiply');
      expect(multiplyArrow).toBeDefined();
      expect(multiplyArrow?.signature).toContain('const multiply = (x, y) =>');

      // Async functions
      const fetchData = symbols.find(s => s.name === 'fetchData');
      expect(fetchData).toBeDefined();
      expect(fetchData?.kind).toBe(SymbolKind.Function);
      expect(fetchData?.signature).toContain('async function fetchData(url)');

      const asyncArrow = symbols.find(s => s.name === 'asyncArrow');
      expect(asyncArrow).toBeDefined();
      expect(asyncArrow?.signature).toContain('const asyncArrow = async (id) =>');

      // Generator functions
      const fibonacci = symbols.find(s => s.name === 'fibonacci');
      expect(fibonacci).toBeDefined();
      expect(fibonacci?.kind).toBe(SymbolKind.Function);
      expect(fibonacci?.signature).toContain('function* fibonacci()');

      const generatorArrow = symbols.find(s => s.name === 'generatorArrow');
      expect(generatorArrow).toBeDefined();
      expect(generatorArrow?.signature).toContain('const generatorArrow = function* (items)');

      // Class with modern features
      const eventEmitter = symbols.find(s => s.name === 'EventEmitter');
      expect(eventEmitter).toBeDefined();
      expect(eventEmitter?.kind).toBe(SymbolKind.Class);

      // Private field
      const listeners = symbols.find(s => s.name === '#listeners');
      expect(listeners).toBeDefined();
      expect(listeners?.kind).toBe(SymbolKind.Field);
      expect(listeners?.parentId).toBe(eventEmitter?.id);

      // Constructor
      const constructor = symbols.find(s => s.name === 'constructor' && s.parentId === eventEmitter?.id);
      expect(constructor).toBeDefined();
      expect(constructor?.kind).toBe(SymbolKind.Constructor);

      // Static method
      const createStatic = symbols.find(s => s.name === 'create' && s.signature?.includes('static'));
      expect(createStatic).toBeDefined();
      expect(createStatic?.kind).toBe(SymbolKind.Method);

      // Getter/setter
      const listenerCount = symbols.find(s => s.name === 'listenerCount' && s.signature?.includes('get'));
      expect(listenerCount).toBeDefined();
      expect(listenerCount?.kind).toBe(SymbolKind.Method);

      const maxListenersSetter = symbols.find(s => s.name === 'maxListeners' && s.signature?.includes('set'));
      expect(maxListenersSetter).toBeDefined();

      // Private method
      const validateEvent = symbols.find(s => s.name === '#validateEvent');
      expect(validateEvent).toBeDefined();
      expect(validateEvent?.kind).toBe(SymbolKind.Method);
      expect(validateEvent?.visibility).toBe('private');

      // Inheritance
      const asyncEventEmitter = symbols.find(s => s.name === 'AsyncEventEmitter');
      expect(asyncEventEmitter).toBeDefined();
      expect(asyncEventEmitter?.signature).toContain('extends EventEmitter');
    });
  });

  describe('Legacy JavaScript Patterns', () => {
    it('should extract function declarations, prototypes, and IIFE patterns', async () => {
      const jsCode = `
// Function declarations
function Calculator(initialValue) {
  this.value = initialValue || 0;
  this.history = [];
}

// Prototype methods
Calculator.prototype.add = function(num) {
  this.value += num;
  this.history.push(\`+\${num}\`);
  return this;
};

Calculator.prototype.subtract = function(num) {
  this.value -= num;
  this.history.push(\`-\${num}\`);
  return this;
};

// Static method on constructor
Calculator.create = function(initialValue) {
  return new Calculator(initialValue);
};

// IIFE (Immediately Invoked Function Expression)
const MathUtils = (function() {
  const PI = 3.14159;
  let precision = 2;

  function roundToPrecision(num) {
    return Math.round(num * Math.pow(10, precision)) / Math.pow(10, precision);
  }

  return {
    constants: {
      PI: PI,
      E: 2.71828
    },

    area: {
      circle: function(radius) {
        return roundToPrecision(PI * radius * radius);
      },
      rectangle: function(width, height) {
        return roundToPrecision(width * height);
      }
    },

    setPrecision: function(newPrecision) {
      precision = Math.max(0, newPrecision);
    },

    getPrecision: function() {
      return precision;
    }
  };
})();

// Traditional function expressions
var multiply = function(a, b) {
  return a * b;
};

var divide = function divide(a, b) {
  if (b === 0) {
    throw new Error('Division by zero');
  }
  return a / b;
};

// Object literal with methods
const ApiClient = {
  baseUrl: 'https://api.example.com',
  timeout: 5000,

  get: function(endpoint) {
    return this.request('GET', endpoint);
  },

  post: function(endpoint, data) {
    return this.request('POST', endpoint, data);
  },

  request: function(method, endpoint, data) {
    // Implementation
    return Promise.resolve({ method, endpoint, data });
  }
};

// Constructor function with closure
function Counter(initialValue) {
  let count = initialValue || 0;

  this.increment = function() {
    return ++count;
  };

  this.decrement = function() {
    return --count;
  };

  this.getValue = function() {
    return count;
  };

  this.reset = function() {
    count = initialValue || 0;
    return count;
  };
}
`;

      const result = await parserManager.parseFile('legacy.js', jsCode);
      const extractor = new JavaScriptExtractor('javascript', 'legacy.js', jsCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Constructor function
      const calculator = symbols.find(s => s.name === 'Calculator');
      expect(calculator).toBeDefined();
      expect(calculator?.kind).toBe(SymbolKind.Function);
      expect(calculator?.signature).toContain('function Calculator(initialValue)');

      // Prototype methods
      const prototypeAdd = symbols.find(s => s.name === 'add' && s.signature?.includes('prototype'));
      expect(prototypeAdd).toBeDefined();
      expect(prototypeAdd?.kind).toBe(SymbolKind.Method);
      expect(prototypeAdd?.signature).toContain('Calculator.prototype.add = function(num)');

      const prototypeSubtract = symbols.find(s => s.name === 'subtract' && s.signature?.includes('prototype'));
      expect(prototypeSubtract).toBeDefined();

      // Static method on constructor
      const calculatorCreate = symbols.find(s => s.name === 'create' && s.signature?.includes('Calculator.create'));
      expect(calculatorCreate).toBeDefined();
      expect(calculatorCreate?.kind).toBe(SymbolKind.Method);

      // IIFE variable
      const mathUtils = symbols.find(s => s.name === 'MathUtils');
      expect(mathUtils).toBeDefined();
      expect(mathUtils?.kind).toBe(SymbolKind.Variable);

      // Functions inside IIFE
      const roundToPrecision = symbols.find(s => s.name === 'roundToPrecision');
      expect(roundToPrecision).toBeDefined();
      expect(roundToPrecision?.kind).toBe(SymbolKind.Function);

      // Function expressions
      const multiplyFn = symbols.find(s => s.name === 'multiply' && s.signature?.includes('var multiply'));
      expect(multiplyFn).toBeDefined();
      expect(multiplyFn?.kind).toBe(SymbolKind.Function);

      const divideFn = symbols.find(s => s.name === 'divide' && s.signature?.includes('function divide'));
      expect(divideFn).toBeDefined();

      // Object literal
      const apiClient = symbols.find(s => s.name === 'ApiClient');
      expect(apiClient).toBeDefined();
      expect(apiClient?.kind).toBe(SymbolKind.Variable);

      // Object methods
      const getMethod = symbols.find(s => s.name === 'get' && s.parentId === apiClient?.id);
      expect(getMethod).toBeDefined();
      expect(getMethod?.kind).toBe(SymbolKind.Method);

      const postMethod = symbols.find(s => s.name === 'post' && s.parentId === apiClient?.id);
      expect(postMethod).toBeDefined();

      // Constructor with closure
      const counter = symbols.find(s => s.name === 'Counter');
      expect(counter).toBeDefined();
      expect(counter?.signature).toContain('function Counter(initialValue)');

      // Closure methods
      const increment = symbols.find(s => s.name === 'increment' && s.signature?.includes('this.increment'));
      expect(increment).toBeDefined();
      expect(increment?.kind).toBe(SymbolKind.Method);
    });
  });

  describe('Modern JavaScript Modules and Destructuring', () => {
    it('should extract destructuring, rest/spread, and template literals', async () => {
      const jsCode = `
// Destructuring imports
import { createElement as h, Fragment } from 'react';
import { connect, Provider } from 'react-redux';

// Dynamic imports
const loadModule = async () => {
  const { utils } = await import('./utils.js');
  return utils;
};

// Destructuring assignments
const user = { name: 'John', age: 30, email: 'john@example.com' };
const { name, age, ...rest } = user;
const [first, second, ...remaining] = [1, 2, 3, 4, 5];

// Destructuring parameters
function processUser({ name, age = 18, ...preferences }) {
  return {
    displayName: name.toUpperCase(),
    isAdult: age >= 18,
    preferences
  };
}

const processArray = ([head, ...tail]) => {
  return { head, tail };
};

// Rest and spread in functions
function sum(...numbers) {
  return numbers.reduce((total, num) => total + num, 0);
}

const combineArrays = (arr1, arr2, ...others) => {
  return [...arr1, ...arr2, ...others.flat()];
};

// Template literals and tagged templates
const formatUser = (user) => \`
  Name: \${user.name}
  Age: \${user.age}
  Email: \${user.email || 'Not provided'}
\`;

function sql(strings, ...values) {
  return strings.reduce((query, string, i) => {
    return query + string + (values[i] || '');
  }, '');
}

const query = sql\`
  SELECT * FROM users
  WHERE age > \${minAge}
  AND status = \${status}
\`;

// Object shorthand and computed properties
const createConfig = (env, debug = false) => {
  const apiUrl = env === 'production' ? 'https://api.prod.com' : 'https://api.dev.com';

  return {
    env,
    debug,
    apiUrl,
    [\`\${env}_settings\`]: {
      caching: env === 'production',
      logging: debug
    },

    // Method shorthand
    init() {
      console.log(\`Initializing \${env} environment\`);
    },

    async connect() {
      const response = await fetch(this.apiUrl);
      return response.json();
    }
  };
};

// Default parameters and destructuring
const createUser = (
  name = 'Anonymous',
  { age = 0, email = null, preferences = {} } = {},
  ...roles
) => {
  return {
    id: Math.random().toString(36),
    name,
    age,
    email,
    preferences: { theme: 'light', ...preferences },
    roles: ['user', ...roles]
  };
};

// Async iterators and generators
async function* fetchPages(baseUrl) {
  let page = 1;
  let hasMore = true;

  while (hasMore) {
    const response = await fetch(\`\${baseUrl}?page=\${page}\`);
    const data = await response.json();

    yield data.items;

    hasMore = data.hasNextPage;
    page++;
  }
}
`;

      const result = await parserManager.parseFile('modern.js', jsCode);
      const extractor = new JavaScriptExtractor('javascript', 'modern.js', jsCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Destructuring imports with aliases
      const reactImport = symbols.find(s => s.name === 'createElement' && s.signature?.includes('as h'));
      expect(reactImport).toBeDefined();
      expect(reactImport?.kind).toBe(SymbolKind.Import);

      // Dynamic import function
      const loadModule = symbols.find(s => s.name === 'loadModule');
      expect(loadModule).toBeDefined();
      expect(loadModule?.signature).toContain('const loadModule = async () =>');

      // Destructuring variables
      const nameVar = symbols.find(s => s.name === 'name' && s.signature?.includes('const { name'));
      expect(nameVar).toBeDefined();
      expect(nameVar?.kind).toBe(SymbolKind.Variable);

      const restVar = symbols.find(s => s.name === 'rest' && s.signature?.includes('...rest'));
      expect(restVar).toBeDefined();

      // Destructuring parameters function
      const processUser = symbols.find(s => s.name === 'processUser');
      expect(processUser).toBeDefined();
      expect(processUser?.signature).toContain('function processUser({ name, age = 18, ...preferences })');

      const processArray = symbols.find(s => s.name === 'processArray');
      expect(processArray).toBeDefined();
      expect(processArray?.signature).toContain('const processArray = ([head, ...tail]) =>');

      // Rest parameters
      const sumFn = symbols.find(s => s.name === 'sum');
      expect(sumFn).toBeDefined();
      expect(sumFn?.signature).toContain('function sum(...numbers)');

      const combineArrays = symbols.find(s => s.name === 'combineArrays');
      expect(combineArrays).toBeDefined();
      expect(combineArrays?.signature).toContain('const combineArrays = (arr1, arr2, ...others) =>');

      // Template literal function
      const formatUser = symbols.find(s => s.name === 'formatUser');
      expect(formatUser).toBeDefined();
      expect(formatUser?.signature).toContain('const formatUser = (user) =>');

      // Tagged template function
      const sql = symbols.find(s => s.name === 'sql');
      expect(sql).toBeDefined();
      expect(sql?.signature).toContain('function sql(strings, ...values)');

      // Object with computed properties and shorthand methods
      const createConfig = symbols.find(s => s.name === 'createConfig');
      expect(createConfig).toBeDefined();
      expect(createConfig?.signature).toContain('const createConfig = (env, debug = false) =>');

      // Default parameters with destructuring
      const createUser = symbols.find(s => s.name === 'createUser');
      expect(createUser).toBeDefined();
      expect(createUser?.signature).toContain('const createUser = (');

      // Async generator
      const fetchPages = symbols.find(s => s.name === 'fetchPages');
      expect(fetchPages).toBeDefined();
      expect(fetchPages?.signature).toContain('async function* fetchPages(baseUrl)');
    });
  });

  describe('Hoisting and Scoping', () => {
    it('should handle var hoisting, let/const block scope, and function hoisting', async () => {
      const jsCode = `
// Function hoisting - can be called before declaration
console.log(hoistedFunction()); // Works

function hoistedFunction() {
  return 'I am hoisted!';
}

// Var hoisting
console.log(hoistedVar); // undefined, not error
var hoistedVar = 'Now I have a value';

// Let/const temporal dead zone
function scopingExample() {
  // console.log(blockScoped); // Would throw ReferenceError

  let blockScoped = 'let variable';
  const constantValue = 'const variable';

  if (true) {
    let blockScoped = 'different block scoped'; // Shadows outer
    const anotherConstant = 'block constant';
    var functionScoped = 'var in block'; // Hoisted to function scope

    function innerFunction() {
      return blockScoped + ' from inner';
    }

    console.log(innerFunction());
  }

  console.log(functionScoped); // Accessible due to var hoisting
  // console.log(anotherConstant); // Would throw ReferenceError
}

// Function expressions are not hoisted
// console.log(notHoisted()); // Would throw TypeError

var notHoisted = function() {
  return 'Function expression';
};

const alsoNotHoisted = function() {
  return 'Function expression with const';
};

// Arrow functions are not hoisted
const arrowNotHoisted = () => {
  return 'Arrow function';
};

// Different scoping behaviors
function demonstrateScoping() {
  // All these create function-scoped variables
  for (var i = 0; i < 3; i++) {
    setTimeout(function() {
      console.log('var:', i); // Prints 3, 3, 3
    }, 100);
  }

  // Block-scoped variables
  for (let j = 0; j < 3; j++) {
    setTimeout(function() {
      console.log('let:', j); // Prints 0, 1, 2
    }, 200);
  }

  // Const in loops
  for (const k of [0, 1, 2]) {
    setTimeout(function() {
      console.log('const:', k); // Prints 0, 1, 2
    }, 300);
  }
}

// Closure with different scoping
function createClosures() {
  const closures = [];

  // Problematic with var
  for (var m = 0; m < 3; m++) {
    closures.push(function() {
      return m; // All return 3
    });
  }

  // Fixed with let
  for (let n = 0; n < 3; n++) {
    closures.push(function() {
      return n; // Returns 0, 1, 2 respectively
    });
  }

  // IIFE solution for var
  for (var p = 0; p < 3; p++) {
    closures.push((function(index) {
      return function() {
        return index;
      };
    })(p));
  }

  return closures;
}
`;

      const result = await parserManager.parseFile('scoping.js', jsCode);
      const extractor = new JavaScriptExtractor('javascript', 'scoping.js', jsCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Hoisted function declaration
      const hoistedFunction = symbols.find(s => s.name === 'hoistedFunction');
      expect(hoistedFunction).toBeDefined();
      expect(hoistedFunction?.kind).toBe(SymbolKind.Function);
      expect(hoistedFunction?.signature).toContain('function hoistedFunction()');

      // Var declaration
      const hoistedVar = symbols.find(s => s.name === 'hoistedVar');
      expect(hoistedVar).toBeDefined();
      expect(hoistedVar?.kind).toBe(SymbolKind.Variable);

      // Function with block scoping
      const scopingExample = symbols.find(s => s.name === 'scopingExample');
      expect(scopingExample).toBeDefined();
      expect(scopingExample?.signature).toContain('function scopingExample()');

      // Inner function
      const innerFunction = symbols.find(s => s.name === 'innerFunction');
      expect(innerFunction).toBeDefined();
      expect(innerFunction?.kind).toBe(SymbolKind.Function);

      // Function expressions
      const notHoisted = symbols.find(s => s.name === 'notHoisted');
      expect(notHoisted).toBeDefined();
      expect(notHoisted?.signature).toContain('var notHoisted = function()');

      const alsoNotHoisted = symbols.find(s => s.name === 'alsoNotHoisted');
      expect(alsoNotHoisted).toBeDefined();
      expect(alsoNotHoisted?.signature).toContain('const alsoNotHoisted = function()');

      // Arrow function
      const arrowNotHoisted = symbols.find(s => s.name === 'arrowNotHoisted');
      expect(arrowNotHoisted).toBeDefined();
      expect(arrowNotHoisted?.signature).toContain('const arrowNotHoisted = () =>');

      // Scoping demonstration function
      const demonstrateScoping = symbols.find(s => s.name === 'demonstrateScoping');
      expect(demonstrateScoping).toBeDefined();

      // Closure creation function
      const createClosures = symbols.find(s => s.name === 'createClosures');
      expect(createClosures).toBeDefined();
      expect(createClosures?.signature).toContain('function createClosures()');
    });
  });

  describe('Error Handling and Strict Mode', () => {
    it('should extract try-catch blocks, error classes, and strict mode indicators', async () => {
      const jsCode = `
'use strict';

// Custom error classes
class ValidationError extends Error {
  constructor(message, field) {
    super(message);
    this.name = 'ValidationError';
    this.field = field;
  }
}

class NetworkError extends Error {
  constructor(message, statusCode, url) {
    super(message);
    this.name = 'NetworkError';
    this.statusCode = statusCode;
    this.url = url;
  }

  static fromResponse(response) {
    return new NetworkError(
      \`HTTP \${response.status}: \${response.statusText}\`,
      response.status,
      response.url
    );
  }
}

// Error handling utilities
function validateUser(user) {
  if (!user) {
    throw new ValidationError('User is required');
  }

  if (!user.email) {
    throw new ValidationError('Email is required', 'email');
  }

  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(user.email)) {
    throw new ValidationError('Invalid email format', 'email');
  }

  return true;
}

async function fetchWithRetry(url, options = {}, maxRetries = 3) {
  let lastError;

  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      const response = await fetch(url, options);

      if (!response.ok) {
        throw NetworkError.fromResponse(response);
      }

      return await response.json();
    } catch (error) {
      lastError = error;

      if (error instanceof NetworkError && error.statusCode < 500) {
        // Don't retry client errors
        throw error;
      }

      if (attempt === maxRetries) {
        throw new NetworkError(
          \`Failed after \${maxRetries} attempts: \${lastError.message}\`,
          0,
          url
        );
      }

      // Exponential backoff
      await new Promise(resolve =>
        setTimeout(resolve, Math.pow(2, attempt) * 1000)
      );
    }
  }
}

// Error boundary function
function withErrorHandling(fn) {
  return async function(...args) {
    try {
      return await fn.apply(this, args);
    } catch (error) {
      console.error('Error in', fn.name, ':', error);

      if (error instanceof ValidationError) {
        return { error: 'validation', message: error.message, field: error.field };
      }

      if (error instanceof NetworkError) {
        return { error: 'network', message: error.message, status: error.statusCode };
      }

      return { error: 'unknown', message: 'An unexpected error occurred' };
    }
  };
}

// Finally block example
function processFile(filename) {
  let file = null;

  try {
    file = openFile(filename);

    if (!file) {
      throw new Error(\`Unable to open file: \${filename}\`);
    }

    const content = file.read();

    if (content.length === 0) {
      throw new Error('File is empty');
    }

    return JSON.parse(content);
  } catch (error) {
    if (error instanceof SyntaxError) {
      throw new ValidationError(\`Invalid JSON in file: \${filename}\`);
    }

    throw error;
  } finally {
    if (file) {
      file.close();
    }
  }
}

// Multiple catch blocks simulation (not native JS, but common pattern)
function handleMultipleErrors(operation) {
  try {
    return operation();
  } catch (error) {
    switch (error.constructor) {
      case ValidationError:
        logValidationError(error);
        break;

      case NetworkError:
        logNetworkError(error);
        break;

      case TypeError:
        logTypeError(error);
        break;

      default:
        logUnknownError(error);
    }

    throw error;
  }
}

function logValidationError(error) {
  console.warn('Validation failed:', error.message);
}

function logNetworkError(error) {
  console.error('Network error:', error.message, 'Status:', error.statusCode);
}

function logTypeError(error) {
  console.error('Type error:', error.message);
}

function logUnknownError(error) {
  console.error('Unknown error:', error);
}
`;

      const result = await parserManager.parseFile('errors.js', jsCode);
      const extractor = new JavaScriptExtractor('javascript', 'errors.js', jsCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Custom error classes
      const validationError = symbols.find(s => s.name === 'ValidationError');
      expect(validationError).toBeDefined();
      expect(validationError?.kind).toBe(SymbolKind.Class);
      expect(validationError?.signature).toContain('class ValidationError extends Error');

      const networkError = symbols.find(s => s.name === 'NetworkError');
      expect(networkError).toBeDefined();
      expect(networkError?.signature).toContain('class NetworkError extends Error');

      // Static method
      const fromResponse = symbols.find(s => s.name === 'fromResponse' && s.signature?.includes('static'));
      expect(fromResponse).toBeDefined();
      expect(fromResponse?.kind).toBe(SymbolKind.Method);

      // Error handling functions
      const validateUser = symbols.find(s => s.name === 'validateUser');
      expect(validateUser).toBeDefined();
      expect(validateUser?.signature).toContain('function validateUser(user)');

      const fetchWithRetry = symbols.find(s => s.name === 'fetchWithRetry');
      expect(fetchWithRetry).toBeDefined();
      expect(fetchWithRetry?.signature).toContain('async function fetchWithRetry');

      // Error boundary
      const withErrorHandling = symbols.find(s => s.name === 'withErrorHandling');
      expect(withErrorHandling).toBeDefined();
      expect(withErrorHandling?.signature).toContain('function withErrorHandling(fn)');

      // Finally block function
      const processFile = symbols.find(s => s.name === 'processFile');
      expect(processFile).toBeDefined();
      expect(processFile?.signature).toContain('function processFile(filename)');

      // Multiple error handling
      const handleMultipleErrors = symbols.find(s => s.name === 'handleMultipleErrors');
      expect(handleMultipleErrors).toBeDefined();

      // Logging functions
      const logValidationError = symbols.find(s => s.name === 'logValidationError');
      expect(logValidationError).toBeDefined();

      const logNetworkError = symbols.find(s => s.name === 'logNetworkError');
      expect(logNetworkError).toBeDefined();

      const logTypeError = symbols.find(s => s.name === 'logTypeError');
      expect(logTypeError).toBeDefined();

      const logUnknownError = symbols.find(s => s.name === 'logUnknownError');
      expect(logUnknownError).toBeDefined();
    });
  });

  describe('Node.js and Browser APIs', () => {
    it('should extract CommonJS modules, require statements, and global APIs', async () => {
      const jsCode = `
// CommonJS exports and requires
const fs = require('fs');
const path = require('path');
const { promisify } = require('util');
const { EventEmitter } = require('events');

// Mixed module syntax (transpiled code)
const express = require('express');
import chalk from 'chalk';

// Module exports
module.exports = {
  createServer,
  middleware,
  utils: {
    formatDate,
    validateInput
  }
};

// Named exports
exports.logger = createLogger();
exports.config = loadConfig();

// Global APIs and polyfills
const globalThis = globalThis || global || window || self;

if (typeof window !== 'undefined') {
  // Browser environment
  window.myLibrary = {
    version: '1.0.0',
    init: initBrowser
  };

  // DOM APIs
  document.addEventListener('DOMContentLoaded', initBrowser);

  // Browser storage
  const storage = {
    set: (key, value) => localStorage.setItem(key, JSON.stringify(value)),
    get: (key) => JSON.parse(localStorage.getItem(key) || 'null'),
    remove: (key) => localStorage.removeItem(key)
  };

} else if (typeof global !== 'undefined') {
  // Node.js environment
  global.myLibrary = {
    version: '1.0.0',
    init: initNode
  };

  // Process APIs
  process.on('exit', cleanup);
  process.on('SIGINT', gracefulShutdown);
  process.on('uncaughtException', handleUncaughtException);
}

// Server creation
function createServer(options = {}) {
  const app = express();

  // Middleware setup
  app.use(express.json());
  app.use(express.urlencoded({ extended: true }));
  app.use(middleware.cors());
  app.use(middleware.logging());

  // Routes
  app.get('/health', (req, res) => {
    res.json({ status: 'healthy', timestamp: new Date().toISOString() });
  });

  app.post('/api/data', async (req, res) => {
    try {
      const validated = validateInput(req.body);
      const result = await processData(validated);
      res.json({ success: true, data: result });
    } catch (error) {
      res.status(400).json({ success: false, error: error.message });
    }
  });

  return app;
}

// Middleware functions
const middleware = {
  cors: () => (req, res, next) => {
    res.header('Access-Control-Allow-Origin', '*');
    res.header('Access-Control-Allow-Methods', 'GET, POST, PUT, DELETE, OPTIONS');
    res.header('Access-Control-Allow-Headers', 'Content-Type, Authorization');
    next();
  },

  logging: () => (req, res, next) => {
    const start = Date.now();

    res.on('finish', () => {
      const duration = Date.now() - start;
      console.log(\`\${req.method} \${req.url} - \${res.statusCode} [\${duration}ms]\`);
    });

    next();
  },

  auth: (options = {}) => (req, res, next) => {
    const token = req.header('Authorization')?.replace('Bearer ', '');

    if (!token) {
      return res.status(401).json({ error: 'No token provided' });
    }

    try {
      const decoded = verifyToken(token, options.secret);
      req.user = decoded;
      next();
    } catch (error) {
      res.status(401).json({ error: 'Invalid token' });
    }
  }
};

// Utility functions
function formatDate(date, format = 'ISO') {
  if (format === 'ISO') {
    return date.toISOString();
  }

  return date.toLocaleDateString();
}

function validateInput(data) {
  if (!data || typeof data !== 'object') {
    throw new Error('Invalid input data');
  }

  return data;
}

function createLogger() {
  return {
    info: (message) => console.log(\`[INFO] \${message}\`),
    warn: (message) => console.warn(\`[WARN] \${message}\`),
    error: (message) => console.error(\`[ERROR] \${message}\`)
  };
}

function loadConfig() {
  return {
    port: process.env.PORT || 3000,
    database: {
      host: process.env.DB_HOST || 'localhost',
      port: process.env.DB_PORT || 5432
    }
  };
}

function initBrowser() {
  console.log('Initializing browser environment');
}

function initNode() {
  console.log('Initializing Node.js environment');
}

function cleanup() {
  console.log('Cleaning up resources...');
}

function gracefulShutdown() {
  console.log('Received SIGINT, shutting down gracefully...');
  process.exit(0);
}

function handleUncaughtException(error) {
  console.error('Uncaught exception:', error);
  process.exit(1);
}

async function processData(data) {
  // Simulate async processing
  await new Promise(resolve => setTimeout(resolve, 100));
  return { processed: true, ...data };
}

function verifyToken(token, secret) {
  // Simplified token verification
  return { userId: '123', username: 'user' };
}
`;

      const result = await parserManager.parseFile('server.js', jsCode);
      const extractor = new JavaScriptExtractor('javascript', 'server.js', jsCode);
      const symbols = extractor.extractSymbols(result.tree);

      // CommonJS requires
      const fsRequire = symbols.find(s => s.name === 'fs' && s.signature?.includes('require'));
      expect(fsRequire).toBeDefined();
      expect(fsRequire?.kind).toBe(SymbolKind.Import);

      const pathRequire = symbols.find(s => s.name === 'path' && s.signature?.includes('require'));
      expect(pathRequire).toBeDefined();

      const promisifyRequire = symbols.find(s => s.name === 'promisify' && s.signature?.includes('require'));
      expect(promisifyRequire).toBeDefined();

      // Mixed module syntax
      const expressRequire = symbols.find(s => s.name === 'express' && s.signature?.includes('require'));
      expect(expressRequire).toBeDefined();

      const chalkImport = symbols.find(s => s.name === 'chalk' && s.signature?.includes('import'));
      expect(chalkImport).toBeDefined();

      // Main server function
      const createServer = symbols.find(s => s.name === 'createServer');
      expect(createServer).toBeDefined();
      expect(createServer?.signature).toContain('function createServer(options = {})');

      // Middleware object
      const middlewareObj = symbols.find(s => s.name === 'middleware');
      expect(middlewareObj).toBeDefined();
      expect(middlewareObj?.kind).toBe(SymbolKind.Variable);

      // Middleware methods
      const corsMiddleware = symbols.find(s => s.name === 'cors' && s.parentId === middlewareObj?.id);
      expect(corsMiddleware).toBeDefined();
      expect(corsMiddleware?.kind).toBe(SymbolKind.Method);

      const loggingMiddleware = symbols.find(s => s.name === 'logging' && s.parentId === middlewareObj?.id);
      expect(loggingMiddleware).toBeDefined();

      const authMiddleware = symbols.find(s => s.name === 'auth' && s.parentId === middlewareObj?.id);
      expect(authMiddleware).toBeDefined();

      // Utility functions
      const formatDate = symbols.find(s => s.name === 'formatDate');
      expect(formatDate).toBeDefined();
      expect(formatDate?.signature).toContain('function formatDate(date, format = \'ISO\')');

      const validateInput = symbols.find(s => s.name === 'validateInput');
      expect(validateInput).toBeDefined();

      const createLogger = symbols.find(s => s.name === 'createLogger');
      expect(createLogger).toBeDefined();

      const loadConfig = symbols.find(s => s.name === 'loadConfig');
      expect(loadConfig).toBeDefined();

      // Environment-specific functions
      const initBrowser = symbols.find(s => s.name === 'initBrowser');
      expect(initBrowser).toBeDefined();

      const initNode = symbols.find(s => s.name === 'initNode');
      expect(initNode).toBeDefined();

      // Process handlers
      const cleanup = symbols.find(s => s.name === 'cleanup');
      expect(cleanup).toBeDefined();

      const gracefulShutdown = symbols.find(s => s.name === 'gracefulShutdown');
      expect(gracefulShutdown).toBeDefined();

      const handleUncaughtException = symbols.find(s => s.name === 'handleUncaughtException');
      expect(handleUncaughtException).toBeDefined();

      // Async functions
      const processData = symbols.find(s => s.name === 'processData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('async function processData(data)');

      const verifyToken = symbols.find(s => s.name === 'verifyToken');
      expect(verifyToken).toBeDefined();
    });
  });
});