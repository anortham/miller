import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { CExtractor } from '../../extractors/c-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('CExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Basic C Structures and Declarations', () => {
    it('should extract functions, variables, structs, and basic declarations', async () => {
      const cCode = `
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "custom_header.h"

// Preprocessor definitions
#define MAX_SIZE 1024
#define MIN(a, b) ((a) < (b) ? (a) : (b))
#define MAX(a, b) ((a) > (b) ? (a) : (b))
#define DEBUG 1

#if DEBUG
#define LOG(msg) printf("DEBUG: %s\\n", msg)
#else
#define LOG(msg)
#endif

// Type definitions
typedef int ErrorCode;
typedef unsigned long long uint64_t;
typedef char* String;

typedef struct Point {
    double x;
    double y;
} Point;

typedef struct Rectangle {
    Point top_left;
    Point bottom_right;
    int color;
} Rectangle;

// Enumerations
enum Status {
    STATUS_SUCCESS = 0,
    STATUS_ERROR = 1,
    STATUS_PENDING = 2,
    STATUS_TIMEOUT = 3
};

typedef enum {
    LOG_LEVEL_DEBUG,
    LOG_LEVEL_INFO,
    LOG_LEVEL_WARNING,
    LOG_LEVEL_ERROR
} LogLevel;

// Global variables
int global_counter = 0;
static int static_counter = 0;
extern int external_counter;
const double PI = 3.14159265359;
volatile int interrupt_flag = 0;

char global_buffer[MAX_SIZE];
Point origin = {0.0, 0.0};
Rectangle default_rect = {{0, 0}, {100, 100}, 0xFFFFFF};

// Function declarations
int add(int a, int b);
double calculate_distance(Point p1, Point p2);
char* allocate_string(size_t length);
void free_string(char* str);
ErrorCode process_data(const char* input, char* output, size_t max_length);

// Simple function definitions
int add(int a, int b) {
    return a + b;
}

int subtract(int a, int b) {
    LOG("Performing subtraction");
    return a - b;
}

double multiply(double x, double y) {
    return x * y;
}

// Function with complex parameters
ErrorCode process_data(const char* input, char* output, size_t max_length) {
    if (input == NULL || output == NULL) {
        return STATUS_ERROR;
    }

    size_t input_len = strlen(input);
    if (input_len >= max_length) {
        return STATUS_ERROR;
    }

    strcpy(output, input);
    return STATUS_SUCCESS;
}

// Function with pointer parameters
void swap_integers(int* a, int* b) {
    if (a == NULL || b == NULL) return;

    int temp = *a;
    *a = *b;
    *b = temp;
}

// Function with array parameters
double sum_array(const double arr[], int count) {
    double total = 0.0;
    for (int i = 0; i < count; i++) {
        total += arr[i];
    }
    return total;
}

// Function with variable arguments
#include <stdarg.h>

int sum_variadic(int count, ...) {
    va_list args;
    va_start(args, count);

    int total = 0;
    for (int i = 0; i < count; i++) {
        total += va_arg(args, int);
    }

    va_end(args);
    return total;
}

// Static function
static void internal_helper() {
    static int call_count = 0;
    call_count++;
    printf("Helper called %d times\\n", call_count);
}

// Inline function (C99)
inline int square(int x) {
    return x * x;
}

// Function returning pointer
char* create_greeting(const char* name) {
    if (name == NULL) return NULL;

    size_t name_len = strlen(name);
    size_t greeting_len = name_len + 20; // "Hello, " + name + "!"

    char* greeting = malloc(greeting_len);
    if (greeting == NULL) {
        return NULL;
    }

    snprintf(greeting, greeting_len, "Hello, %s!", name);
    return greeting;
}

// Function with function pointer parameter
typedef int (*CompareFn)(const void* a, const void* b);

void sort_array(void* base, size_t count, size_t size, CompareFn compare) {
    // Simplified bubble sort implementation
    for (size_t i = 0; i < count - 1; i++) {
        for (size_t j = 0; j < count - i - 1; j++) {
            char* elem1 = (char*)base + j * size;
            char* elem2 = (char*)base + (j + 1) * size;

            if (compare(elem1, elem2) > 0) {
                // Swap elements
                for (size_t k = 0; k < size; k++) {
                    char temp = elem1[k];
                    elem1[k] = elem2[k];
                    elem2[k] = temp;
                }
            }
        }
    }
}

// Comparison functions
int compare_integers(const void* a, const void* b) {
    int ia = *(const int*)a;
    int ib = *(const int*)b;
    return (ia > ib) - (ia < ib);
}

int compare_strings(const void* a, const void* b) {
    const char* sa = *(const char**)a;
    const char* sb = *(const char**)b;
    return strcmp(sa, sb);
}

// Main function
int main(int argc, char* argv[]) {
    printf("Program started with %d arguments\\n", argc);

    // Test basic operations
    int x = 10, y = 20;
    int sum = add(x, y);
    int diff = subtract(x, y);

    printf("Sum: %d, Difference: %d\\n", sum, diff);

    // Test string operations
    char* greeting = create_greeting("World");
    if (greeting != NULL) {
        printf("%s\\n", greeting);
        free(greeting);
    }

    // Test array operations
    double numbers[] = {1.5, 2.3, 3.7, 4.1, 5.9};
    int count = sizeof(numbers) / sizeof(numbers[0]);
    double total = sum_array(numbers, count);
    printf("Array sum: %.2f\\n", total);

    // Test variadic function
    int var_sum = sum_variadic(5, 1, 2, 3, 4, 5);
    printf("Variadic sum: %d\\n", var_sum);

    return STATUS_SUCCESS;
}
`;

      const result = await parserManager.parseFile('basic.c', cCode);
      const extractor = new CExtractor('c', 'basic.c', cCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Include statements
      const stdioInclude = symbols.find(s => s.signature?.includes('#include <stdio.h>'));
      expect(stdioInclude).toBeDefined();
      expect(stdioInclude?.kind).toBe(SymbolKind.Import);

      const customInclude = symbols.find(s => s.signature?.includes('#include "custom_header.h"'));
      expect(customInclude).toBeDefined();

      // Macro definitions
      const maxSizeMacro = symbols.find(s => s.name === 'MAX_SIZE');
      expect(maxSizeMacro).toBeDefined();
      expect(maxSizeMacro?.kind).toBe(SymbolKind.Constant);
      expect(maxSizeMacro?.signature).toContain('#define MAX_SIZE 1024');

      const minMacro = symbols.find(s => s.name === 'MIN');
      expect(minMacro).toBeDefined();
      expect(minMacro?.signature).toContain('#define MIN(a, b)');

      const debugMacro = symbols.find(s => s.name === 'DEBUG');
      expect(debugMacro).toBeDefined();

      const logMacro = symbols.find(s => s.name === 'LOG');
      expect(logMacro).toBeDefined();

      // Typedefs
      const errorCodeTypedef = symbols.find(s => s.name === 'ErrorCode');
      expect(errorCodeTypedef).toBeDefined();
      expect(errorCodeTypedef?.kind).toBe(SymbolKind.Type);
      expect(errorCodeTypedef?.signature).toContain('typedef int ErrorCode');

      const uint64Typedef = symbols.find(s => s.name === 'uint64_t');
      expect(uint64Typedef).toBeDefined();

      const stringTypedef = symbols.find(s => s.name === 'String');
      expect(stringTypedef).toBeDefined();

      const pointTypedef = symbols.find(s => s.name === 'Point');
      expect(pointTypedef).toBeDefined();
      expect(pointTypedef?.signature).toContain('typedef struct Point');

      // Struct definitions
      const pointStruct = symbols.find(s => s.signature?.includes('struct Point') && s.signature?.includes('double x'));
      expect(pointStruct).toBeDefined();
      expect(pointStruct?.kind).toBe(SymbolKind.Class); // Structs as classes

      const rectangleStruct = symbols.find(s => s.signature?.includes('struct Rectangle'));
      expect(rectangleStruct).toBeDefined();

      // Enum definitions
      const statusEnum = symbols.find(s => s.name === 'Status');
      expect(statusEnum).toBeDefined();
      expect(statusEnum?.kind).toBe(SymbolKind.Enum);

      const logLevelEnum = symbols.find(s => s.name === 'LogLevel');
      expect(logLevelEnum).toBeDefined();

      // Enum values
      const statusSuccess = symbols.find(s => s.name === 'STATUS_SUCCESS');
      expect(statusSuccess).toBeDefined();
      expect(statusSuccess?.kind).toBe(SymbolKind.Constant);

      const logDebug = symbols.find(s => s.name === 'LOG_LEVEL_DEBUG');
      expect(logDebug).toBeDefined();

      // Global variables
      const globalCounter = symbols.find(s => s.name === 'global_counter');
      expect(globalCounter).toBeDefined();
      expect(globalCounter?.kind).toBe(SymbolKind.Variable);
      expect(globalCounter?.signature).toContain('int global_counter = 0');

      const staticCounter = symbols.find(s => s.name === 'static_counter');
      expect(staticCounter).toBeDefined();
      expect(staticCounter?.signature).toContain('static int static_counter');

      const externalCounter = symbols.find(s => s.name === 'external_counter');
      expect(externalCounter).toBeDefined();
      expect(externalCounter?.signature).toContain('extern int external_counter');

      const piConstant = symbols.find(s => s.name === 'PI');
      expect(piConstant).toBeDefined();
      expect(piConstant?.signature).toContain('const double PI');

      const volatileFlag = symbols.find(s => s.name === 'interrupt_flag');
      expect(volatileFlag).toBeDefined();
      expect(volatileFlag?.signature).toContain('volatile int interrupt_flag');

      // Array variables
      const globalBuffer = symbols.find(s => s.name === 'global_buffer');
      expect(globalBuffer).toBeDefined();
      expect(globalBuffer?.signature).toContain('char global_buffer[MAX_SIZE]');

      const origin = symbols.find(s => s.name === 'origin');
      expect(origin).toBeDefined();
      expect(origin?.signature).toContain('Point origin = {0.0, 0.0}');

      // Function declarations
      const addFunction = symbols.find(s => s.name === 'add');
      expect(addFunction).toBeDefined();
      expect(addFunction?.kind).toBe(SymbolKind.Function);
      expect(addFunction?.signature).toContain('int add(int a, int b)');

      const subtractFunction = symbols.find(s => s.name === 'subtract');
      expect(subtractFunction).toBeDefined();

      const multiplyFunction = symbols.find(s => s.name === 'multiply');
      expect(multiplyFunction).toBeDefined();
      expect(multiplyFunction?.signature).toContain('double multiply(double x, double y)');

      // Complex parameter functions
      const processDataFunction = symbols.find(s => s.name === 'process_data');
      expect(processDataFunction).toBeDefined();
      expect(processDataFunction?.signature).toContain('ErrorCode process_data(const char* input');

      const swapFunction = symbols.find(s => s.name === 'swap_integers');
      expect(swapFunction).toBeDefined();
      expect(swapFunction?.signature).toContain('void swap_integers(int* a, int* b)');

      const sumArrayFunction = symbols.find(s => s.name === 'sum_array');
      expect(sumArrayFunction).toBeDefined();
      expect(sumArrayFunction?.signature).toContain('double sum_array(const double arr[], int count)');

      // Variadic function
      const sumVariadicFunction = symbols.find(s => s.name === 'sum_variadic');
      expect(sumVariadicFunction).toBeDefined();
      expect(sumVariadicFunction?.signature).toContain('int sum_variadic(int count, ...)');

      // Static function
      const internalHelper = symbols.find(s => s.name === 'internal_helper');
      expect(internalHelper).toBeDefined();
      expect(internalHelper?.signature).toContain('static void internal_helper()');

      // Inline function
      const squareFunction = symbols.find(s => s.name === 'square');
      expect(squareFunction).toBeDefined();
      expect(squareFunction?.signature).toContain('inline int square(int x)');

      // Function returning pointer
      const createGreeting = symbols.find(s => s.name === 'create_greeting');
      expect(createGreeting).toBeDefined();
      expect(createGreeting?.signature).toContain('char* create_greeting(const char* name)');

      // Function pointer typedef
      const compareFnTypedef = symbols.find(s => s.name === 'CompareFn');
      expect(compareFnTypedef).toBeDefined();
      expect(compareFnTypedef?.signature).toContain('typedef int (*CompareFn)');

      // Function with function pointer parameter
      const sortArrayFunction = symbols.find(s => s.name === 'sort_array');
      expect(sortArrayFunction).toBeDefined();
      expect(sortArrayFunction?.signature).toContain('CompareFn compare');

      // Comparison functions
      const compareIntegers = symbols.find(s => s.name === 'compare_integers');
      expect(compareIntegers).toBeDefined();

      const compareStrings = symbols.find(s => s.name === 'compare_strings');
      expect(compareStrings).toBeDefined();

      // Main function
      const mainFunction = symbols.find(s => s.name === 'main');
      expect(mainFunction).toBeDefined();
      expect(mainFunction?.signature).toContain('int main(int argc, char* argv[])');
    });
  });

  describe('Advanced C Features and Memory Management', () => {
    it('should extract complex structs, unions, function pointers, and memory operations', async () => {
      const cCode = `
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdbool.h>
#include <complex.h>

// Advanced preprocessor usage
#define STRINGIFY(x) #x
#define CONCAT(a, b) a##b
#define ARRAY_SIZE(arr) (sizeof(arr) / sizeof((arr)[0]))

#ifdef __cplusplus
extern "C" {
#endif

// Forward declarations
struct Node;
typedef struct Node Node;

// Complex data structures
typedef struct {
    int id;
    char name[64];
    double balance;
    bool active;
    void* user_data;
} Account;

typedef union {
    int int_value;
    float float_value;
    char char_value;
    void* pointer_value;
    struct {
        uint16_t low;
        uint16_t high;
    } word;
} Value;

// Linked list node
struct Node {
    int data;
    struct Node* next;
    struct Node* prev;
};

// Binary tree node
typedef struct TreeNode {
    int value;
    struct TreeNode* left;
    struct TreeNode* right;
    int height;
} TreeNode;

// Function pointer types
typedef int (*BinaryOperation)(int a, int b);
typedef void (*Callback)(void* context, int result);
typedef bool (*Predicate)(const void* item);

// Struct with function pointers (virtual table pattern)
typedef struct {
    void* data;
    size_t size;
    void (*destroy)(void* data);
    void* (*clone)(const void* data);
    int (*compare)(const void* a, const void* b);
    char* (*to_string)(const void* data);
} GenericObject;

// Bit fields
typedef struct {
    unsigned int is_valid : 1;
    unsigned int is_dirty : 1;
    unsigned int level : 4;
    unsigned int type : 8;
    unsigned int reserved : 18;
} Flags;

typedef struct {
    uint32_t ip;
    uint16_t port;
    uint8_t protocol;
    Flags flags;
} NetworkPacket;

// Memory pool allocator
typedef struct MemoryBlock {
    void* data;
    size_t size;
    bool in_use;
    struct MemoryBlock* next;
} MemoryBlock;

typedef struct {
    MemoryBlock* blocks;
    size_t total_size;
    size_t used_size;
    size_t block_count;
} MemoryPool;

// Function pointer arrays and tables
typedef struct {
    const char* name;
    BinaryOperation operation;
} OperationEntry;

// Global function pointer table
OperationEntry operation_table[] = {
    {"add", add_operation},
    {"subtract", subtract_operation},
    {"multiply", multiply_operation},
    {"divide", divide_operation},
    {NULL, NULL}
};

// Memory management functions
MemoryPool* create_memory_pool(size_t total_size) {
    MemoryPool* pool = malloc(sizeof(MemoryPool));
    if (pool == NULL) {
        return NULL;
    }

    pool->blocks = malloc(sizeof(MemoryBlock));
    if (pool->blocks == NULL) {
        free(pool);
        return NULL;
    }

    pool->blocks->data = malloc(total_size);
    if (pool->blocks->data == NULL) {
        free(pool->blocks);
        free(pool);
        return NULL;
    }

    pool->blocks->size = total_size;
    pool->blocks->in_use = false;
    pool->blocks->next = NULL;
    pool->total_size = total_size;
    pool->used_size = 0;
    pool->block_count = 1;

    return pool;
}

void* pool_allocate(MemoryPool* pool, size_t size) {
    if (pool == NULL || size == 0) {
        return NULL;
    }

    MemoryBlock* current = pool->blocks;
    while (current != NULL) {
        if (!current->in_use && current->size >= size) {
            // Split block if necessary
            if (current->size > size + sizeof(MemoryBlock)) {
                MemoryBlock* new_block = malloc(sizeof(MemoryBlock));
                if (new_block != NULL) {
                    new_block->data = (char*)current->data + size;
                    new_block->size = current->size - size;
                    new_block->in_use = false;
                    new_block->next = current->next;

                    current->next = new_block;
                    current->size = size;
                    pool->block_count++;
                }
            }

            current->in_use = true;
            pool->used_size += current->size;
            return current->data;
        }
        current = current->next;
    }

    return NULL; // No suitable block found
}

void pool_free(MemoryPool* pool, void* ptr) {
    if (pool == NULL || ptr == NULL) {
        return;
    }

    MemoryBlock* current = pool->blocks;
    while (current != NULL) {
        if (current->data == ptr && current->in_use) {
            current->in_use = false;
            pool->used_size -= current->size;

            // Coalesce adjacent free blocks
            MemoryBlock* next = current->next;
            while (next != NULL && !next->in_use) {
                current->size += next->size;
                current->next = next->next;
                free(next);
                pool->block_count--;
                next = current->next;
            }

            return;
        }
        current = current->next;
    }
}

void destroy_memory_pool(MemoryPool* pool) {
    if (pool == NULL) {
        return;
    }

    MemoryBlock* current = pool->blocks;
    while (current != NULL) {
        MemoryBlock* next = current->next;
        free(current->data);
        free(current);
        current = next;
    }

    free(pool);
}

// Linked list operations
Node* create_node(int data) {
    Node* node = malloc(sizeof(Node));
    if (node == NULL) {
        return NULL;
    }

    node->data = data;
    node->next = NULL;
    node->prev = NULL;
    return node;
}

void insert_node(Node** head, int data) {
    Node* new_node = create_node(data);
    if (new_node == NULL) {
        return;
    }

    if (*head == NULL) {
        *head = new_node;
    } else {
        new_node->next = *head;
        (*head)->prev = new_node;
        *head = new_node;
    }
}

void delete_node(Node** head, int data) {
    Node* current = *head;

    while (current != NULL) {
        if (current->data == data) {
            if (current->prev != NULL) {
                current->prev->next = current->next;
            } else {
                *head = current->next;
            }

            if (current->next != NULL) {
                current->next->prev = current->prev;
            }

            free(current);
            return;
        }
        current = current->next;
    }
}

void free_list(Node* head) {
    while (head != NULL) {
        Node* next = head->next;
        free(head);
        head = next;
    }
}

// Binary tree operations
TreeNode* create_tree_node(int value) {
    TreeNode* node = malloc(sizeof(TreeNode));
    if (node == NULL) {
        return NULL;
    }

    node->value = value;
    node->left = NULL;
    node->right = NULL;
    node->height = 1;
    return node;
}

int get_height(TreeNode* node) {
    return node ? node->height : 0;
}

int get_balance(TreeNode* node) {
    return node ? get_height(node->left) - get_height(node->right) : 0;
}

TreeNode* rotate_right(TreeNode* y) {
    TreeNode* x = y->left;
    TreeNode* T2 = x->right;

    x->right = y;
    y->left = T2;

    y->height = 1 + MAX(get_height(y->left), get_height(y->right));
    x->height = 1 + MAX(get_height(x->left), get_height(x->right));

    return x;
}

TreeNode* rotate_left(TreeNode* x) {
    TreeNode* y = x->right;
    TreeNode* T2 = y->left;

    y->left = x;
    x->right = T2;

    x->height = 1 + MAX(get_height(x->left), get_height(x->right));
    y->height = 1 + MAX(get_height(y->left), get_height(y->right));

    return y;
}

// Operation implementations
int add_operation(int a, int b) {
    return a + b;
}

int subtract_operation(int a, int b) {
    return a - b;
}

int multiply_operation(int a, int b) {
    return a * b;
}

int divide_operation(int a, int b) {
    return b != 0 ? a / b : 0;
}

// Generic object implementations
void int_destroy(void* data) {
    free(data);
}

void* int_clone(const void* data) {
    int* original = (int*)data;
    int* copy = malloc(sizeof(int));
    if (copy != NULL) {
        *copy = *original;
    }
    return copy;
}

int int_compare(const void* a, const void* b) {
    int ia = *(const int*)a;
    int ib = *(const int*)b;
    return (ia > ib) - (ia < ib);
}

char* int_to_string(const void* data) {
    int value = *(const int*)data;
    char* str = malloc(32);
    if (str != NULL) {
        snprintf(str, 32, "%d", value);
    }
    return str;
}

GenericObject create_int_object(int value) {
    GenericObject obj;
    obj.data = malloc(sizeof(int));
    if (obj.data != NULL) {
        *(int*)obj.data = value;
    }
    obj.size = sizeof(int);
    obj.destroy = int_destroy;
    obj.clone = int_clone;
    obj.compare = int_compare;
    obj.to_string = int_to_string;
    return obj;
}

// Signal handling and system programming
#include <signal.h>
#include <unistd.h>

volatile sig_atomic_t shutdown_flag = 0;

void signal_handler(int signum) {
    switch (signum) {
        case SIGINT:
        case SIGTERM:
            shutdown_flag = 1;
            break;
        default:
            break;
    }
}

void setup_signal_handlers() {
    signal(SIGINT, signal_handler);
    signal(SIGTERM, signal_handler);
}

// File I/O with error handling
int read_file_to_buffer(const char* filename, char** buffer, size_t* size) {
    FILE* file = fopen(filename, "rb");
    if (file == NULL) {
        return -1;
    }

    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        return -1;
    }

    long file_size = ftell(file);
    if (file_size < 0) {
        fclose(file);
        return -1;
    }

    if (fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return -1;
    }

    *buffer = malloc(file_size + 1);
    if (*buffer == NULL) {
        fclose(file);
        return -1;
    }

    size_t bytes_read = fread(*buffer, 1, file_size, file);
    if (bytes_read != (size_t)file_size) {
        free(*buffer);
        *buffer = NULL;
        fclose(file);
        return -1;
    }

    (*buffer)[file_size] = '\0';
    *size = file_size;

    fclose(file);
    return 0;
}

#ifdef __cplusplus
}
#endif
`;

      const result = await parserManager.parseFile('advanced.c', cCode);
      const extractor = new CExtractor('c', 'advanced.c', cCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Advanced macros
      const stringifyMacro = symbols.find(s => s.name === 'STRINGIFY');
      expect(stringifyMacro).toBeDefined();
      expect(stringifyMacro?.signature).toContain('#define STRINGIFY(x) #x');

      const concatMacro = symbols.find(s => s.name === 'CONCAT');
      expect(concatMacro).toBeDefined();

      const arraySizeMacro = symbols.find(s => s.name === 'ARRAY_SIZE');
      expect(arraySizeMacro).toBeDefined();

      // Complex structs
      const accountStruct = symbols.find(s => s.name === 'Account');
      expect(accountStruct).toBeDefined();
      expect(accountStruct?.kind).toBe(SymbolKind.Class);
      expect(accountStruct?.signature).toContain('typedef struct');

      const nodeStruct = symbols.find(s => s.name === 'Node');
      expect(nodeStruct).toBeDefined();

      const treeNodeStruct = symbols.find(s => s.name === 'TreeNode');
      expect(treeNodeStruct).toBeDefined();

      // Union types
      const valueUnion = symbols.find(s => s.name === 'Value');
      expect(valueUnion).toBeDefined();
      expect(valueUnion?.signature).toContain('typedef union');

      // Function pointer typedefs
      const binaryOpTypedef = symbols.find(s => s.name === 'BinaryOperation');
      expect(binaryOpTypedef).toBeDefined();
      expect(binaryOpTypedef?.signature).toContain('typedef int (*BinaryOperation)');

      const callbackTypedef = symbols.find(s => s.name === 'Callback');
      expect(callbackTypedef).toBeDefined();

      const predicateTypedef = symbols.find(s => s.name === 'Predicate');
      expect(predicateTypedef).toBeDefined();

      // Struct with function pointers
      const genericObjectStruct = symbols.find(s => s.name === 'GenericObject');
      expect(genericObjectStruct).toBeDefined();
      expect(genericObjectStruct?.signature).toContain('void (*destroy)');
      expect(genericObjectStruct?.signature).toContain('void* (*clone)');

      // Bit fields
      const flagsStruct = symbols.find(s => s.name === 'Flags');
      expect(flagsStruct).toBeDefined();
      expect(flagsStruct?.signature).toContain('unsigned int is_valid : 1');

      const networkPacketStruct = symbols.find(s => s.name === 'NetworkPacket');
      expect(networkPacketStruct).toBeDefined();

      // Memory management structures
      const memoryBlockStruct = symbols.find(s => s.name === 'MemoryBlock');
      expect(memoryBlockStruct).toBeDefined();

      const memoryPoolStruct = symbols.find(s => s.name === 'MemoryPool');
      expect(memoryPoolStruct).toBeDefined();

      // Operation table struct
      const operationEntryStruct = symbols.find(s => s.name === 'OperationEntry');
      expect(operationEntryStruct).toBeDefined();

      // Global arrays
      const operationTable = symbols.find(s => s.name === 'operation_table');
      expect(operationTable).toBeDefined();
      expect(operationTable?.signature).toContain('OperationEntry operation_table[]');

      // Memory management functions
      const createMemoryPool = symbols.find(s => s.name === 'create_memory_pool');
      expect(createMemoryPool).toBeDefined();
      expect(createMemoryPool?.signature).toContain('MemoryPool* create_memory_pool(size_t total_size)');

      const poolAllocate = symbols.find(s => s.name === 'pool_allocate');
      expect(poolAllocate).toBeDefined();
      expect(poolAllocate?.signature).toContain('void* pool_allocate(MemoryPool* pool, size_t size)');

      const poolFree = symbols.find(s => s.name === 'pool_free');
      expect(poolFree).toBeDefined();

      const destroyMemoryPool = symbols.find(s => s.name === 'destroy_memory_pool');
      expect(destroyMemoryPool).toBeDefined();

      // Linked list functions
      const createNode = symbols.find(s => s.name === 'create_node');
      expect(createNode).toBeDefined();
      expect(createNode?.signature).toContain('Node* create_node(int data)');

      const insertNode = symbols.find(s => s.name === 'insert_node');
      expect(insertNode).toBeDefined();
      expect(insertNode?.signature).toContain('void insert_node(Node** head, int data)');

      const deleteNode = symbols.find(s => s.name === 'delete_node');
      expect(deleteNode).toBeDefined();

      const freeList = symbols.find(s => s.name === 'free_list');
      expect(freeList).toBeDefined();

      // Tree functions
      const createTreeNode = symbols.find(s => s.name === 'create_tree_node');
      expect(createTreeNode).toBeDefined();

      const getHeight = symbols.find(s => s.name === 'get_height');
      expect(getHeight).toBeDefined();

      const getBalance = symbols.find(s => s.name === 'get_balance');
      expect(getBalance).toBeDefined();

      const rotateRight = symbols.find(s => s.name === 'rotate_right');
      expect(rotateRight).toBeDefined();
      expect(rotateRight?.signature).toContain('TreeNode* rotate_right(TreeNode* y)');

      const rotateLeft = symbols.find(s => s.name === 'rotate_left');
      expect(rotateLeft).toBeDefined();

      // Operation implementations
      const addOperation = symbols.find(s => s.name === 'add_operation');
      expect(addOperation).toBeDefined();

      const subtractOperation = symbols.find(s => s.name === 'subtract_operation');
      expect(subtractOperation).toBeDefined();

      const multiplyOperation = symbols.find(s => s.name === 'multiply_operation');
      expect(multiplyOperation).toBeDefined();

      const divideOperation = symbols.find(s => s.name === 'divide_operation');
      expect(divideOperation).toBeDefined();

      // Generic object functions
      const intDestroy = symbols.find(s => s.name === 'int_destroy');
      expect(intDestroy).toBeDefined();

      const intClone = symbols.find(s => s.name === 'int_clone');
      expect(intClone).toBeDefined();

      const intCompare = symbols.find(s => s.name === 'int_compare');
      expect(intCompare).toBeDefined();

      const intToString = symbols.find(s => s.name === 'int_to_string');
      expect(intToString).toBeDefined();

      const createIntObject = symbols.find(s => s.name === 'create_int_object');
      expect(createIntObject).toBeDefined();

      // Signal handling
      const shutdownFlag = symbols.find(s => s.name === 'shutdown_flag');
      expect(shutdownFlag).toBeDefined();
      expect(shutdownFlag?.signature).toContain('volatile sig_atomic_t shutdown_flag');

      const signalHandler = symbols.find(s => s.name === 'signal_handler');
      expect(signalHandler).toBeDefined();
      expect(signalHandler?.signature).toContain('void signal_handler(int signum)');

      const setupSignalHandlers = symbols.find(s => s.name === 'setup_signal_handlers');
      expect(setupSignalHandlers).toBeDefined();

      // File I/O functions
      const readFileToBuffer = symbols.find(s => s.name === 'read_file_to_buffer');
      expect(readFileToBuffer).toBeDefined();
      expect(readFileToBuffer?.signature).toContain('int read_file_to_buffer(const char* filename, char** buffer, size_t* size)');

      // Extern "C" handling
      const externC = symbols.filter(s => s.signature?.includes('extern "C"'));
      expect(externC.length).toBeGreaterThanOrEqual(1);

      // Standard library includes
      const stdintInclude = symbols.find(s => s.signature?.includes('#include <stdint.h>'));
      expect(stdintInclude).toBeDefined();

      const stdboolInclude = symbols.find(s => s.signature?.includes('#include <stdbool.h>'));
      expect(stdboolInclude).toBeDefined();

      const complexInclude = symbols.find(s => s.signature?.includes('#include <complex.h>'));
      expect(complexInclude).toBeDefined();

      const signalInclude = symbols.find(s => s.signature?.includes('#include <signal.h>'));
      expect(signalInclude).toBeDefined();

      const unistdInclude = symbols.find(s => s.signature?.includes('#include <unistd.h>'));
      expect(unistdInclude).toBeDefined();
    });
  });

  describe('C Preprocessor and Conditional Compilation', () => {
    it('should extract preprocessor directives, conditional compilation, and pragma directives', async () => {
      const cCode = `
// Compiler and platform detection
#ifdef __GNUC__
    #define COMPILER_GCC 1
    #define LIKELY(x) __builtin_expect(!!(x), 1)
    #define UNLIKELY(x) __builtin_expect(!!(x), 0)
    #define FORCE_INLINE __attribute__((always_inline)) inline
    #define PACKED __attribute__((packed))
#elif defined(_MSC_VER)
    #define COMPILER_MSVC 1
    #define LIKELY(x) (x)
    #define UNLIKELY(x) (x)
    #define FORCE_INLINE __forceinline
    #define PACKED
    #pragma warning(disable: 4996) // Disable deprecated function warnings
#else
    #define COMPILER_UNKNOWN 1
    #define LIKELY(x) (x)
    #define UNLIKELY(x) (x)
    #define FORCE_INLINE inline
    #define PACKED
#endif

// Platform detection
#if defined(_WIN32) || defined(_WIN64)
    #define PLATFORM_WINDOWS 1
    #include <windows.h>
    typedef HANDLE FileHandle;
    #define INVALID_FILE_HANDLE INVALID_HANDLE_VALUE
#elif defined(__linux__)
    #define PLATFORM_LINUX 1
    #include <unistd.h>
    #include <fcntl.h>
    typedef int FileHandle;
    #define INVALID_FILE_HANDLE -1
#elif defined(__APPLE__)
    #define PLATFORM_MACOS 1
    #include <unistd.h>
    #include <fcntl.h>
    typedef int FileHandle;
    #define INVALID_FILE_HANDLE -1
#else
    #define PLATFORM_UNKNOWN 1
    typedef void* FileHandle;
    #define INVALID_FILE_HANDLE NULL
#endif

// Architecture detection
#if defined(__x86_64__) || defined(_M_X64)
    #define ARCH_X64 1
    #define CACHE_LINE_SIZE 64
#elif defined(__i386__) || defined(_M_IX86)
    #define ARCH_X86 1
    #define CACHE_LINE_SIZE 32
#elif defined(__aarch64__)
    #define ARCH_ARM64 1
    #define CACHE_LINE_SIZE 64
#elif defined(__arm__)
    #define ARCH_ARM 1
    #define CACHE_LINE_SIZE 32
#else
    #define ARCH_UNKNOWN 1
    #define CACHE_LINE_SIZE 64
#endif

// Debug/Release configuration
#ifdef NDEBUG
    #define BUILD_RELEASE 1
    #define DEBUG_PRINT(...)
    #define ASSERT(condition)
    #define DEBUG_ONLY(code)
#else
    #define BUILD_DEBUG 1
    #define DEBUG_PRINT(...) printf(__VA_ARGS__)
    #define ASSERT(condition) \\
        do { \\
            if (!(condition)) { \\
                fprintf(stderr, "Assertion failed: %s at %s:%d\\n", \\
                        #condition, __FILE__, __LINE__); \\
                abort(); \\
            } \\
        } while(0)
    #define DEBUG_ONLY(code) code
#endif

// Version information
#define VERSION_MAJOR 2
#define VERSION_MINOR 1
#define VERSION_PATCH 3
#define VERSION_STRING STRINGIFY(VERSION_MAJOR) "." \\
                      STRINGIFY(VERSION_MINOR) "." \\
                      STRINGIFY(VERSION_PATCH)

// Feature toggles
#ifndef FEATURE_NETWORKING
    #define FEATURE_NETWORKING 1
#endif

#ifndef FEATURE_GRAPHICS
    #define FEATURE_GRAPHICS 0
#endif

#ifndef FEATURE_AUDIO
    #define FEATURE_AUDIO 1
#endif

// Conditional feature compilation
#if FEATURE_NETWORKING
    #include <sys/socket.h>
    #include <netinet/in.h>
    #include <arpa/inet.h>

    typedef struct {
        int socket_fd;
        struct sockaddr_in address;
        bool connected;
    } NetworkConnection;

    int network_init(void);
    NetworkConnection* network_connect(const char* host, int port);
    int network_send(NetworkConnection* conn, const void* data, size_t size);
    int network_receive(NetworkConnection* conn, void* buffer, size_t size);
    void network_disconnect(NetworkConnection* conn);
    void network_cleanup(void);
#endif

#if FEATURE_GRAPHICS
    #include <GL/gl.h>

    typedef struct {
        int width;
        int height;
        void* window_handle;
        bool fullscreen;
    } GraphicsContext;

    int graphics_init(int width, int height);
    void graphics_clear(void);
    void graphics_present(void);
    void graphics_shutdown(void);
#endif

#if FEATURE_AUDIO
    typedef struct {
        float* samples;
        size_t sample_count;
        int sample_rate;
        int channels;
    } AudioBuffer;

    int audio_init(int sample_rate, int channels);
    AudioBuffer* audio_load_file(const char* filename);
    void audio_play_buffer(const AudioBuffer* buffer);
    void audio_free_buffer(AudioBuffer* buffer);
    void audio_shutdown(void);
#endif

// Pragma directives
#pragma once
#pragma pack(push, 1)

typedef struct {
    uint8_t type;
    uint16_t length;
    uint32_t data;
} PACKED NetworkHeader;

#pragma pack(pop)

// Alignment directives
#ifdef COMPILER_GCC
    #define ALIGN(n) __attribute__((aligned(n)))
#elif defined(COMPILER_MSVC)
    #define ALIGN(n) __declspec(align(n))
#else
    #define ALIGN(n)
#endif

typedef struct ALIGN(CACHE_LINE_SIZE) {
    volatile int counter;
    char padding[CACHE_LINE_SIZE - sizeof(int)];
} AtomicCounter;

// Inline assembly (GCC specific)
#ifdef COMPILER_GCC
    static inline uint64_t rdtsc(void) {
        uint32_t low, high;
        asm volatile ("rdtsc" : "=a" (low), "=d" (high));
        return ((uint64_t)high << 32) | low;
    }

    static inline void cpu_pause(void) {
        asm volatile ("pause" ::: "memory");
    }
#else
    static inline uint64_t rdtsc(void) {
        return 0; // Fallback implementation
    }

    static inline void cpu_pause(void) {
        // No-op on non-GCC compilers
    }
#endif

// Advanced macro techniques
#define GET_MACRO(_1,_2,_3,_4,NAME,...) NAME
#define VARIADIC_MACRO(...) GET_MACRO(__VA_ARGS__, MACRO4, MACRO3, MACRO2, MACRO1)(__VA_ARGS__)

#define MACRO1(a) single_arg_function(a)
#define MACRO2(a,b) two_arg_function(a,b)
#define MACRO3(a,b,c) three_arg_function(a,b,c)
#define MACRO4(a,b,c,d) four_arg_function(a,b,c,d)

// X-Macro pattern for enum/string mapping
#define ERROR_CODES(X) \\
    X(ERROR_NONE, "No error") \\
    X(ERROR_INVALID_PARAM, "Invalid parameter") \\
    X(ERROR_OUT_OF_MEMORY, "Out of memory") \\
    X(ERROR_FILE_NOT_FOUND, "File not found") \\
    X(ERROR_PERMISSION_DENIED, "Permission denied") \\
    X(ERROR_NETWORK_FAILURE, "Network failure")

// Generate enum
#define ENUM_ENTRY(name, desc) name,
typedef enum {
    ERROR_CODES(ENUM_ENTRY)
    ERROR_COUNT
} ErrorCode;
#undef ENUM_ENTRY

// Generate string array
#define STRING_ENTRY(name, desc) desc,
static const char* error_strings[] = {
    ERROR_CODES(STRING_ENTRY)
};
#undef STRING_ENTRY

// Function to get error string
const char* get_error_string(ErrorCode code) {
    if (code < 0 || code >= ERROR_COUNT) {
        return "Unknown error";
    }
    return error_strings[code];
}

// Stringification and token pasting
#define DECLARE_GETTER_SETTER(type, name) \\
    type get_##name(void) { return name; } \\
    void set_##name(type value) { name = value; }

// Example usage
static int global_value = 42;
DECLARE_GETTER_SETTER(int, global_value)

// Conditional compilation for different C standards
#if __STDC_VERSION__ >= 201112L
    // C11 features
    #include <stdatomic.h>
    #include <threads.h>

    typedef atomic_int AtomicInt;

    static inline int atomic_increment(AtomicInt* value) {
        return atomic_fetch_add(value, 1) + 1;
    }

    _Static_assert(sizeof(int) == 4, "int must be 4 bytes");
    _Static_assert(CACHE_LINE_SIZE >= 32, "Cache line size too small");

#elif __STDC_VERSION__ >= 199901L
    // C99 features
    typedef volatile int AtomicInt;

    static inline int atomic_increment(AtomicInt* value) {
        return ++(*value); // Not truly atomic, just an example
    }

#else
    // C90 fallback
    typedef int AtomicInt;

    int atomic_increment(AtomicInt* value) {
        return ++(*value);
    }
#endif

// Compiler-specific optimizations
#ifdef COMPILER_GCC
    #define OPTIMIZE_SIZE __attribute__((optimize("Os")))
    #define OPTIMIZE_SPEED __attribute__((optimize("O3")))
    #define NO_OPTIMIZE __attribute__((optimize("O0")))
#else
    #define OPTIMIZE_SIZE
    #define OPTIMIZE_SPEED
    #define NO_OPTIMIZE
#endif

OPTIMIZE_SPEED
int fast_function(int x) {
    return x * x + 2 * x + 1;
}

OPTIMIZE_SIZE
void small_function(void) {
    // Optimized for size
    printf("This function is optimized for size\\n");
}

NO_OPTIMIZE
void debug_function(void) {
    // No optimization for easier debugging
    int x = 10;
    int y = 20;
    int z = x + y;
    printf("Debug: %d + %d = %d\\n", x, y, z);
}
`;

      const result = await parserManager.parseFile('preprocessor.c', cCode);
      const extractor = new CExtractor('c', 'preprocessor.c', cCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Compiler detection macros
      const compilerGcc = symbols.find(s => s.name === 'COMPILER_GCC');
      expect(compilerGcc).toBeDefined();
      expect(compilerGcc?.kind).toBe(SymbolKind.Constant);

      const likelyMacro = symbols.find(s => s.name === 'LIKELY');
      expect(likelyMacro).toBeDefined();
      expect(likelyMacro?.signature).toContain('__builtin_expect');

      const forceInlineMacro = symbols.find(s => s.name === 'FORCE_INLINE');
      expect(forceInlineMacro).toBeDefined();

      const packedMacro = symbols.find(s => s.name === 'PACKED');
      expect(packedMacro).toBeDefined();

      // Platform detection
      const platformWindows = symbols.find(s => s.name === 'PLATFORM_WINDOWS');
      expect(platformWindows).toBeDefined();

      const fileHandleTypedef = symbols.find(s => s.name === 'FileHandle');
      expect(fileHandleTypedef).toBeDefined();

      const invalidFileHandle = symbols.find(s => s.name === 'INVALID_FILE_HANDLE');
      expect(invalidFileHandle).toBeDefined();

      // Architecture detection
      const archX64 = symbols.find(s => s.name === 'ARCH_X64');
      expect(archX64).toBeDefined();

      const cacheLineSize = symbols.find(s => s.name === 'CACHE_LINE_SIZE');
      expect(cacheLineSize).toBeDefined();

      // Debug/Release configuration
      const buildDebug = symbols.find(s => s.name === 'BUILD_DEBUG');
      expect(buildDebug).toBeDefined();

      const debugPrint = symbols.find(s => s.name === 'DEBUG_PRINT');
      expect(debugPrint).toBeDefined();

      const assertMacro = symbols.find(s => s.name === 'ASSERT');
      expect(assertMacro).toBeDefined();

      const debugOnly = symbols.find(s => s.name === 'DEBUG_ONLY');
      expect(debugOnly).toBeDefined();

      // Version information
      const versionMajor = symbols.find(s => s.name === 'VERSION_MAJOR');
      expect(versionMajor).toBeDefined();

      const versionString = symbols.find(s => s.name === 'VERSION_STRING');
      expect(versionString).toBeDefined();

      // Feature toggles
      const featureNetworking = symbols.find(s => s.name === 'FEATURE_NETWORKING');
      expect(featureNetworking).toBeDefined();

      const featureGraphics = symbols.find(s => s.name === 'FEATURE_GRAPHICS');
      expect(featureGraphics).toBeDefined();

      // Conditional structs
      const networkConnection = symbols.find(s => s.name === 'NetworkConnection');
      expect(networkConnection).toBeDefined();

      const audioBuffer = symbols.find(s => s.name === 'AudioBuffer');
      expect(audioBuffer).toBeDefined();

      // Conditional functions
      const networkInit = symbols.find(s => s.name === 'network_init');
      expect(networkInit).toBeDefined();

      const audioInit = symbols.find(s => s.name === 'audio_init');
      expect(audioInit).toBeDefined();

      // Pragma-affected structures
      const networkHeader = symbols.find(s => s.name === 'NetworkHeader');
      expect(networkHeader).toBeDefined();
      expect(networkHeader?.signature).toContain('PACKED');

      // Alignment directives
      const alignMacro = symbols.find(s => s.name === 'ALIGN');
      expect(alignMacro).toBeDefined();

      const atomicCounter = symbols.find(s => s.name === 'AtomicCounter');
      expect(atomicCounter).toBeDefined();
      expect(atomicCounter?.signature).toContain('ALIGN(CACHE_LINE_SIZE)');

      // Inline assembly functions
      const rdtscFunction = symbols.find(s => s.name === 'rdtsc');
      expect(rdtscFunction).toBeDefined();
      expect(rdtscFunction?.signature).toContain('static inline uint64_t rdtsc(void)');

      const cpuPauseFunction = symbols.find(s => s.name === 'cpu_pause');
      expect(cpuPauseFunction).toBeDefined();

      // Advanced macro patterns
      const getMacro = symbols.find(s => s.name === 'GET_MACRO');
      expect(getMacro).toBeDefined();

      const variadicMacro = symbols.find(s => s.name === 'VARIADIC_MACRO');
      expect(variadicMacro).toBeDefined();

      const macro1 = symbols.find(s => s.name === 'MACRO1');
      expect(macro1).toBeDefined();

      // X-Macro pattern
      const errorCodes = symbols.find(s => s.name === 'ERROR_CODES');
      expect(errorCodes).toBeDefined();

      const enumEntry = symbols.find(s => s.name === 'ENUM_ENTRY');
      expect(enumEntry).toBeDefined();

      const errorCodeEnum = symbols.find(s => s.name === 'ErrorCode');
      expect(errorCodeEnum).toBeDefined();

      const errorStrings = symbols.find(s => s.name === 'error_strings');
      expect(errorStrings).toBeDefined();

      const getErrorString = symbols.find(s => s.name === 'get_error_string');
      expect(getErrorString).toBeDefined();

      // Generated getter/setter
      const declareGetterSetter = symbols.find(s => s.name === 'DECLARE_GETTER_SETTER');
      expect(declareGetterSetter).toBeDefined();

      const globalValue = symbols.find(s => s.name === 'global_value');
      expect(globalValue).toBeDefined();

      // C11 specific features
      const atomicInt = symbols.find(s => s.name === 'AtomicInt');
      expect(atomicInt).toBeDefined();

      const atomicIncrement = symbols.find(s => s.name === 'atomic_increment');
      expect(atomicIncrement).toBeDefined();

      // Compiler optimization attributes
      const optimizeSize = symbols.find(s => s.name === 'OPTIMIZE_SIZE');
      expect(optimizeSize).toBeDefined();

      const optimizeSpeed = symbols.find(s => s.name === 'OPTIMIZE_SPEED');
      expect(optimizeSpeed).toBeDefined();

      const noOptimize = symbols.find(s => s.name === 'NO_OPTIMIZE');
      expect(noOptimize).toBeDefined();

      // Optimization-specific functions
      const fastFunction = symbols.find(s => s.name === 'fast_function');
      expect(fastFunction).toBeDefined();

      const smallFunction = symbols.find(s => s.name === 'small_function');
      expect(smallFunction).toBeDefined();

      const debugFunction = symbols.find(s => s.name === 'debug_function');
      expect(debugFunction).toBeDefined();

      // Standard library includes for conditional features
      const socketInclude = symbols.find(s => s.signature?.includes('#include <sys/socket.h>'));
      expect(socketInclude).toBeDefined();

      const stdatomicInclude = symbols.find(s => s.signature?.includes('#include <stdatomic.h>'));
      expect(stdatomicInclude).toBeDefined();

      const threadsInclude = symbols.find(s => s.signature?.includes('#include <threads.h>'));
      expect(threadsInclude).toBeDefined();
    });
  });
});