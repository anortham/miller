import { describe, it, expect, beforeAll } from 'bun:test';
import { GoExtractor } from '../../extractors/go-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('GoExtractor', () => {
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

  describe('Package Extraction', () => {
    it('should extract package declarations', async () => {
      const goCode = `
package main

package utils
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const mainPackage = symbols.find(s => s.name === 'main');
      expect(mainPackage).toBeDefined();
      expect(mainPackage?.kind).toBe(SymbolKind.Namespace);
      expect(mainPackage?.signature).toBe('package main');
      expect(mainPackage?.visibility).toBe('public');
    });
  });

  describe('Type Extraction', () => {
    it('should extract struct definitions', async () => {
      const goCode = `
package main

type User struct {
    ID    int64  \`json:"id"\`
    Name  string \`json:"name"\`
    Email string \`json:"email,omitempty"\`
}

type Point struct {
    X, Y float64
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const userStruct = symbols.find(s => s.name === 'User');
      expect(userStruct).toBeDefined();
      expect(userStruct?.kind).toBe(SymbolKind.Class);
      expect(userStruct?.signature).toBe('type User struct');
      expect(userStruct?.visibility).toBe('public');

      const pointStruct = symbols.find(s => s.name === 'Point');
      expect(pointStruct).toBeDefined();
      expect(pointStruct?.visibility).toBe('public');
    });

    it('should extract interface definitions', async () => {
      const goCode = `
package main

type UserService interface {
    GetUser(id int64) (*User, error)
    CreateUser(user *User) error
    UpdateUser(user *User) error
}

type Reader interface {
    Read([]byte) (int, error)
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const userService = symbols.find(s => s.name === 'UserService');
      expect(userService).toBeDefined();
      expect(userService?.kind).toBe(SymbolKind.Interface);
      expect(userService?.signature).toBe('type UserService interface');
      expect(userService?.visibility).toBe('public');

      const reader = symbols.find(s => s.name === 'Reader');
      expect(reader).toBeDefined();
      expect(reader?.kind).toBe(SymbolKind.Interface);
    });

    it('should extract type aliases', async () => {
      const goCode = `
package main

type UserID int64
type Username string
type Config map[string]interface{}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const userID = symbols.find(s => s.name === 'UserID');
      expect(userID).toBeDefined();
      expect(userID?.kind).toBe(SymbolKind.Type);
      expect(userID?.signature).toContain('type UserID = int64');

      const config = symbols.find(s => s.name === 'Config');
      expect(config).toBeDefined();
      expect(config?.signature).toContain('type Config = map[string]interface{}');
    });
  });

  describe('Function Extraction', () => {
    it('should extract standalone functions', async () => {
      const goCode = `
package main

func Add(a, b int) int {
    return a + b
}

func ProcessUsers(users []User) (<-chan User, error) {
    resultCh := make(chan User)
    return resultCh, nil
}

func main() {
    fmt.Println("Hello, World!")
}

func privateHelper() {
    // private function
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const addFunc = symbols.find(s => s.name === 'Add');
      expect(addFunc).toBeDefined();
      expect(addFunc?.kind).toBe(SymbolKind.Function);
      expect(addFunc?.signature).toContain('func Add(a, b int) int');
      expect(addFunc?.visibility).toBe('public');

      const processFunc = symbols.find(s => s.name === 'ProcessUsers');
      expect(processFunc).toBeDefined();
      expect(processFunc?.signature).toContain('func ProcessUsers');
      expect(processFunc?.signature).toContain('<-chan User');

      const mainFunc = symbols.find(s => s.name === 'main' && s.kind === SymbolKind.Function);
      expect(mainFunc).toBeDefined();
      expect(mainFunc?.visibility).toBe('private'); // lowercase

      const privateFunc = symbols.find(s => s.name === 'privateHelper');
      expect(privateFunc).toBeDefined();
      expect(privateFunc?.visibility).toBe('private');
    });
  });

  describe('Method Extraction', () => {
    it('should extract methods with receivers', async () => {
      const goCode = `
package main

type User struct {
    Name string
    Age  int
}

func (u *User) GetName() string {
    return u.Name
}

func (u User) IsAdult() bool {
    return u.Age >= 18
}

func (u *User) SetName(name string) {
    u.Name = name
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const getName = symbols.find(s => s.name === 'GetName');
      expect(getName).toBeDefined();
      expect(getName?.kind).toBe(SymbolKind.Method);
      expect(getName?.signature).toContain('func (u *User) GetName() string');
      expect(getName?.visibility).toBe('public');

      const isAdult = symbols.find(s => s.name === 'IsAdult');
      expect(isAdult).toBeDefined();
      expect(isAdult?.signature).toContain('func (u User) IsAdult() bool');

      const setName = symbols.find(s => s.name === 'SetName');
      expect(setName).toBeDefined();
      expect(setName?.signature).toContain('func (u *User) SetName(name string)');
    });
  });

  describe('Import Extraction', () => {
    it('should extract import declarations', async () => {
      const goCode = `
package main

import "fmt"
import "net/http"

import (
    "context"
    "encoding/json"
    log "github.com/sirupsen/logrus"
    _ "github.com/lib/pq"
)
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const fmtImport = symbols.find(s => s.name === 'fmt');
      expect(fmtImport).toBeDefined();
      expect(fmtImport?.kind).toBe(SymbolKind.Import);
      expect(fmtImport?.signature).toContain('import "fmt"');

      const httpImport = symbols.find(s => s.name === 'http');
      expect(httpImport).toBeDefined();
      expect(httpImport?.signature).toContain('import "net/http"');

      // Aliased import
      const logImport = symbols.find(s => s.name === 'log');
      expect(logImport).toBeDefined();
      expect(logImport?.signature).toContain('import log "github.com/sirupsen/logrus"');
    });
  });

  describe('Constants and Variables', () => {
    it('should extract constant declarations', async () => {
      const goCode = `
package main

const MaxUsers = 1000
const DefaultTimeout = 30

const (
    StatusActive   = "active"
    StatusInactive = "inactive"
    StatusPending  = "pending"
)
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const maxUsers = symbols.find(s => s.name === 'MaxUsers');
      expect(maxUsers).toBeDefined();
      expect(maxUsers?.kind).toBe(SymbolKind.Constant);
      expect(maxUsers?.signature).toContain('const MaxUsers = 1000');
      expect(maxUsers?.visibility).toBe('public');

      const statusActive = symbols.find(s => s.name === 'StatusActive');
      expect(statusActive).toBeDefined();
      expect(statusActive?.signature).toContain('const StatusActive = "active"');
    });

    it('should extract variable declarations', async () => {
      const goCode = `
package main

var GlobalConfig *Config
var Logger *log.Logger

var (
    Version    = "1.0.0"
    BuildTime  string
    debugMode  bool
)
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const globalConfig = symbols.find(s => s.name === 'GlobalConfig');
      expect(globalConfig).toBeDefined();
      expect(globalConfig?.kind).toBe(SymbolKind.Variable);
      expect(globalConfig?.signature).toContain('var GlobalConfig *Config');
      expect(globalConfig?.visibility).toBe('public');

      const version = symbols.find(s => s.name === 'Version');
      expect(version).toBeDefined();
      expect(version?.signature).toContain('var Version = "1.0.0"');

      const debugMode = symbols.find(s => s.name === 'debugMode');
      expect(debugMode).toBeDefined();
      expect(debugMode?.visibility).toBe('private'); // lowercase
    });
  });

  describe('Go-specific Features', () => {
    it('should handle channel types and goroutines', async () => {
      const goCode = `
package main

func ProcessData(input <-chan string) chan string {
    output := make(chan string)

    go func() {
        defer close(output)
        for data := range input {
            processed := processItem(data)
            output <- processed
        }
    }()

    return output
}

func SendData(ch chan<- string, data string) {
    ch <- data
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const processData = symbols.find(s => s.name === 'ProcessData');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('<-chan string');
      expect(processData?.signature).toContain('chan string');

      const sendData = symbols.find(s => s.name === 'SendData');
      expect(sendData).toBeDefined();
      expect(sendData?.signature).toContain('chan<- string');
    });
  });

  describe('Type Inference', () => {
    it('should infer types from Go annotations', async () => {
      const goCode = `
package main

func GetName() string {
    return "test"
}

func Calculate(x, y int) float64 {
    return float64(x + y)
}

var Count int = 42
var Message string = "hello"
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      const getName = symbols.find(s => s.name === 'GetName');
      expect(getName).toBeDefined();
      expect(types.get(getName!.id)).toBe('string');

      const calculate = symbols.find(s => s.name === 'Calculate');
      expect(calculate).toBeDefined();
      expect(types.get(calculate!.id)).toBe('float64');

      const count = symbols.find(s => s.name === 'Count');
      expect(count).toBeDefined();
      expect(types.get(count!.id)).toBe('int');

      console.log(`🐹 Type inference extracted ${types.size} types`);
    });
  });

  describe('Relationship Extraction', () => {
    it('should extract method-receiver relationships', async () => {
      const goCode = `
package main

type User struct {
    Name string
}

func (u *User) GetName() string {
    return u.Name
}

func (u *User) SetName(name string) {
    u.Name = name
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find method-receiver relationships
      expect(relationships.length).toBeGreaterThanOrEqual(1);

      console.log(`🐹 Found ${relationships.length} Go relationships`);
    });
  });

  describe('Generics and Type Constraints', () => {
    it('should extract generic types and constraints', async () => {
      const goCode = `
package main

import "fmt"

// Generic constraint interface
type Ordered interface {
    ~int | ~int8 | ~int16 | ~int32 | ~int64 |
    ~uint | ~uint8 | ~uint16 | ~uint32 | ~uint64 | ~uintptr |
    ~float32 | ~float64 |
    ~string
}

// Generic struct with type parameter
type Stack[T any] struct {
    items []T
}

// Generic method
func (s *Stack[T]) Push(item T) {
    s.items = append(s.items, item)
}

func (s *Stack[T]) Pop() (T, bool) {
    if len(s.items) == 0 {
        var zero T
        return zero, false
    }
    index := len(s.items) - 1
    item := s.items[index]
    s.items = s.items[:index]
    return item, true
}

// Generic function with constraints
func Max[T Ordered](a, b T) T {
    if a > b {
        return a
    }
    return b
}

// Generic interface
type Comparable[T any] interface {
    Compare(other T) int
}

// Generic map utility
func Map[T, U any](slice []T, fn func(T) U) []U {
    result := make([]U, len(slice))
    for i, v := range slice {
        result[i] = fn(v)
    }
    return result
}

// Generic reduce function
func Reduce[T, U any](slice []T, initial U, fn func(U, T) U) U {
    result := initial
    for _, v := range slice {
        result = fn(result, v)
    }
    return result
}

// Type union constraint
type Numeric interface {
    int | int32 | int64 | float32 | float64
}

func Sum[T Numeric](values ...T) T {
    var sum T
    for _, v := range values {
        sum += v
    }
    return sum
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const ordered = symbols.find(s => s.name === 'Ordered');
      expect(ordered).toBeDefined();
      expect(ordered?.kind).toBe(SymbolKind.Interface);
      expect(ordered?.signature).toContain('type Ordered interface');

      const stack = symbols.find(s => s.name === 'Stack');
      expect(stack).toBeDefined();
      expect(stack?.kind).toBe(SymbolKind.Class);
      expect(stack?.signature).toContain('type Stack[T any] struct');

      const pushMethod = symbols.find(s => s.name === 'Push');
      expect(pushMethod).toBeDefined();
      expect(pushMethod?.signature).toContain('func (s *Stack[T]) Push(item T)');

      const popMethod = symbols.find(s => s.name === 'Pop');
      expect(popMethod).toBeDefined();
      expect(popMethod?.signature).toContain('func (s *Stack[T]) Pop() (T, bool)');

      const maxFunc = symbols.find(s => s.name === 'Max');
      expect(maxFunc).toBeDefined();
      expect(maxFunc?.signature).toContain('func Max[T Ordered](a, b T) T');

      const comparable = symbols.find(s => s.name === 'Comparable');
      expect(comparable).toBeDefined();
      expect(comparable?.signature).toContain('type Comparable[T any] interface');

      const mapFunc = symbols.find(s => s.name === 'Map');
      expect(mapFunc).toBeDefined();
      expect(mapFunc?.signature).toContain('func Map[T, U any]');

      const numeric = symbols.find(s => s.name === 'Numeric');
      expect(numeric).toBeDefined();
      expect(numeric?.signature).toContain('int | int32 | int64 | float32 | float64');

      const sumFunc = symbols.find(s => s.name === 'Sum');
      expect(sumFunc).toBeDefined();
      expect(sumFunc?.signature).toContain('func Sum[T Numeric](values ...T) T');
    });
  });

  describe('Advanced Concurrency Patterns', () => {
    it('should extract concurrency primitives and patterns', async () => {
      const goCode = `
package main

import (
    "context"
    "sync"
    "time"
)

// WorkerPool represents a pool of workers
type WorkerPool struct {
    workers    int
    jobQueue   chan Job
    resultCh   chan Result
    wg         sync.WaitGroup
    mu         sync.RWMutex
    done       chan struct{}
    ctx        context.Context
    cancel     context.CancelFunc
}

// Job represents work to be done
type Job struct {
    ID   int
    Data interface{}
}

// Result represents the result of a job
type Result struct {
    JobID int
    Data  interface{}
    Error error
}

// NewWorkerPool creates a new worker pool
func NewWorkerPool(workers int, bufferSize int) *WorkerPool {
    ctx, cancel := context.WithCancel(context.Background())
    return &WorkerPool{
        workers:  workers,
        jobQueue: make(chan Job, bufferSize),
        resultCh: make(chan Result, bufferSize),
        done:     make(chan struct{}),
        ctx:      ctx,
        cancel:   cancel,
    }
}

// Start starts the worker pool
func (wp *WorkerPool) Start() {
    for i := 0; i < wp.workers; i++ {
        wp.wg.Add(1)
        go wp.worker(i)
    }
}

// worker is the main worker goroutine
func (wp *WorkerPool) worker(id int) {
    defer wp.wg.Done()

    for {
        select {
        case job := <-wp.jobQueue:
            result := wp.processJob(job)
            select {
            case wp.resultCh <- result:
            case <-wp.ctx.Done():
                return
            }
        case <-wp.ctx.Done():
            return
        }
    }
}

// processJob processes a single job
func (wp *WorkerPool) processJob(job Job) Result {
    // Simulate work
    time.Sleep(time.Millisecond * 100)

    return Result{
        JobID: job.ID,
        Data:  job.Data,
        Error: nil,
    }
}

// Submit submits a job to the pool
func (wp *WorkerPool) Submit(job Job) error {
    select {
    case wp.jobQueue <- job:
        return nil
    case <-wp.ctx.Done():
        return wp.ctx.Err()
    }
}

// Results returns the result channel
func (wp *WorkerPool) Results() <-chan Result {
    return wp.resultCh
}

// Shutdown gracefully shuts down the worker pool
func (wp *WorkerPool) Shutdown() {
    wp.cancel()
    close(wp.jobQueue)
    wp.wg.Wait()
    close(wp.resultCh)
    close(wp.done)
}

// Pipeline demonstrates a pipeline pattern
func Pipeline(ctx context.Context, input <-chan int) <-chan string {
    // Stage 1: multiply by 2
    stage1 := make(chan int)
    go func() {
        defer close(stage1)
        for {
            select {
            case v, ok := <-input:
                if !ok {
                    return
                }
                select {
                case stage1 <- v * 2:
                case <-ctx.Done():
                    return
                }
            case <-ctx.Done():
                return
            }
        }
    }()

    // Stage 2: convert to string
    output := make(chan string)
    go func() {
        defer close(output)
        for {
            select {
            case v, ok := <-stage1:
                if !ok {
                    return
                }
                select {
                case output <- fmt.Sprintf("result: %d", v):
                case <-ctx.Done():
                    return
                }
            case <-ctx.Done():
                return
            }
        }
    }()

    return output
}

// FanOut demonstrates fan-out pattern
func FanOut(input <-chan Job, workers int) []<-chan Result {
    channels := make([]<-chan Result, workers)

    for i := 0; i < workers; i++ {
        ch := make(chan Result)
        channels[i] = ch

        go func(output chan<- Result) {
            defer close(output)
            for job := range input {
                // Process job
                result := Result{
                    JobID: job.ID,
                    Data:  job.Data,
                }
                output <- result
            }
        }(ch)
    }

    return channels
}

// FanIn demonstrates fan-in pattern
func FanIn(channels ...<-chan Result) <-chan Result {
    output := make(chan Result)
    var wg sync.WaitGroup

    wg.Add(len(channels))
    for _, ch := range channels {
        go func(c <-chan Result) {
            defer wg.Done()
            for result := range c {
                output <- result
            }
        }(ch)
    }

    go func() {
        wg.Wait()
        close(output)
    }()

    return output
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const workerPool = symbols.find(s => s.name === 'WorkerPool');
      expect(workerPool).toBeDefined();
      expect(workerPool?.kind).toBe(SymbolKind.Class);
      expect(workerPool?.signature).toContain('type WorkerPool struct');

      const job = symbols.find(s => s.name === 'Job');
      expect(job).toBeDefined();
      expect(job?.kind).toBe(SymbolKind.Class);

      const resultSymbol = symbols.find(s => s.name === 'Result');
      expect(resultSymbol).toBeDefined();
      expect(resultSymbol?.kind).toBe(SymbolKind.Class);

      const newWorkerPool = symbols.find(s => s.name === 'NewWorkerPool');
      expect(newWorkerPool).toBeDefined();
      expect(newWorkerPool?.signature).toContain('func NewWorkerPool(workers int, bufferSize int) *WorkerPool');

      const startMethod = symbols.find(s => s.name === 'Start');
      expect(startMethod).toBeDefined();
      expect(startMethod?.signature).toContain('func (wp *WorkerPool) Start()');

      const workerMethod = symbols.find(s => s.name === 'worker');
      expect(workerMethod).toBeDefined();
      expect(workerMethod?.signature).toContain('func (wp *WorkerPool) worker(id int)');

      const submitMethod = symbols.find(s => s.name === 'Submit');
      expect(submitMethod).toBeDefined();
      expect(submitMethod?.signature).toContain('func (wp *WorkerPool) Submit(job Job) error');

      const pipelineFunc = symbols.find(s => s.name === 'Pipeline');
      expect(pipelineFunc).toBeDefined();
      expect(pipelineFunc?.signature).toContain('func Pipeline(ctx context.Context, input <-chan int) <-chan string');

      const fanOutFunc = symbols.find(s => s.name === 'FanOut');
      expect(fanOutFunc).toBeDefined();
      expect(fanOutFunc?.signature).toContain('func FanOut(input <-chan Job, workers int) []<-chan Result');

      const fanInFunc = symbols.find(s => s.name === 'FanIn');
      expect(fanInFunc).toBeDefined();
      expect(fanInFunc?.signature).toContain('func FanIn(channels ...<-chan Result) <-chan Result');
    });
  });

  describe('Error Handling and Custom Types', () => {
    it('should extract custom error types and error handling patterns', async () => {
      const goCode = `
package main

import (
    "errors"
    "fmt"
)

// Custom error types
type ValidationError struct {
    Field   string
    Message string
    Code    int
}

func (e ValidationError) Error() string {
    return fmt.Sprintf("validation error on field '%s': %s (code: %d)", e.Field, e.Message, e.Code)
}

func (e ValidationError) Unwrap() error {
    return errors.New(e.Message)
}

// Custom error with nested error
type DatabaseError struct {
    Operation string
    Err       error
}

func (e DatabaseError) Error() string {
    return fmt.Sprintf("database %s failed: %v", e.Operation, e.Err)
}

func (e DatabaseError) Unwrap() error {
    return e.Err
}

// Error constants
var (
    ErrUserNotFound    = errors.New("user not found")
    ErrInvalidInput    = errors.New("invalid input")
    ErrDatabaseTimeout = errors.New("database operation timed out")
)

// Result type for better error handling
type Result[T any] struct {
    Value T
    Err   error
}

func (r Result[T]) IsOk() bool {
    return r.Err == nil
}

func (r Result[T]) IsErr() bool {
    return r.Err != nil
}

func (r Result[T]) Unwrap() (T, error) {
    return r.Value, r.Err
}

// Ok creates a successful result
func Ok[T any](value T) Result[T] {
    return Result[T]{Value: value}
}

// Err creates an error result
func Err[T any](err error) Result[T] {
    var zero T
    return Result[T]{Value: zero, Err: err}
}

// UserService with comprehensive error handling
type UserService struct {
    repo UserRepository
}

func (s *UserService) GetUser(id int64) Result[*User] {
    if id <= 0 {
        return Err[*User](ValidationError{
            Field:   "id",
            Message: "id must be positive",
            Code:    400,
        })
    }

    user, err := s.repo.FindByID(id)
    if err != nil {
        if errors.Is(err, ErrUserNotFound) {
            return Err[*User](err)
        }
        return Err[*User](DatabaseError{
            Operation: "select",
            Err:       err,
        })
    }

    return Ok(user)
}

func (s *UserService) CreateUser(user *User) Result[*User] {
    if err := s.validateUser(user); err != nil {
        return Err[*User](err)
    }

    createdUser, err := s.repo.Create(user)
    if err != nil {
        return Err[*User](DatabaseError{
            Operation: "insert",
            Err:       err,
        })
    }

    return Ok(createdUser)
}

func (s *UserService) validateUser(user *User) error {
    if user == nil {
        return ValidationError{
            Field:   "user",
            Message: "user cannot be nil",
            Code:    400,
        }
    }

    if user.Name == "" {
        return ValidationError{
            Field:   "name",
            Message: "name is required",
            Code:    400,
        }
    }

    if len(user.Name) > 100 {
        return ValidationError{
            Field:   "name",
            Message: "name too long",
            Code:    400,
        }
    }

    return nil
}

// Error handling utilities
func HandleError(err error) {
    var validationErr ValidationError
    var dbErr DatabaseError

    switch {
    case errors.As(err, &validationErr):
        fmt.Printf("Validation error: %s\n", validationErr.Error())
    case errors.As(err, &dbErr):
        fmt.Printf("Database error: %s\n", dbErr.Error())
    case errors.Is(err, ErrUserNotFound):
        fmt.Printf("User not found\n")
    default:
        fmt.Printf("Unknown error: %v\n", err)
    }
}

// Panic and recover patterns
func SafeOperation() (result string, err error) {
    defer func() {
        if r := recover(); r != nil {
            err = fmt.Errorf("panic recovered: %v", r)
        }
    }()

    // Potentially panicking operation
    riskyOperation()
    return "success", nil
}

func riskyOperation() {
    // This might panic
    panic("something went wrong")
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const validationError = symbols.find(s => s.name === 'ValidationError');
      expect(validationError).toBeDefined();
      expect(validationError?.kind).toBe(SymbolKind.Class);
      expect(validationError?.signature).toContain('type ValidationError struct');

      const errorMethod = symbols.find(s => s.name === 'Error');
      expect(errorMethod).toBeDefined();
      expect(errorMethod?.signature).toContain('func (e ValidationError) Error() string');

      const unwrapMethod = symbols.find(s => s.name === 'Unwrap');
      expect(unwrapMethod).toBeDefined();
      expect(unwrapMethod?.signature).toContain('func (e ValidationError) Unwrap() error');

      const databaseError = symbols.find(s => s.name === 'DatabaseError');
      expect(databaseError).toBeDefined();
      expect(databaseError?.kind).toBe(SymbolKind.Class);

      const resultType = symbols.find(s => s.name === 'Result');
      expect(resultType).toBeDefined();
      expect(resultType?.signature).toContain('type Result[T any] struct');

      const isOkMethod = symbols.find(s => s.name === 'IsOk');
      expect(isOkMethod).toBeDefined();
      expect(isOkMethod?.signature).toContain('func (r Result[T]) IsOk() bool');

      const okFunc = symbols.find(s => s.name === 'Ok');
      expect(okFunc).toBeDefined();
      expect(okFunc?.signature).toContain('func Ok[T any](value T) Result[T]');

      const errFunc = symbols.find(s => s.name === 'Err');
      expect(errFunc).toBeDefined();
      expect(errFunc?.signature).toContain('func Err[T any](err error) Result[T]');

      const userService = symbols.find(s => s.name === 'UserService');
      expect(userService).toBeDefined();
      expect(userService?.kind).toBe(SymbolKind.Class);

      const getUserMethod = symbols.find(s => s.name === 'GetUser');
      expect(getUserMethod).toBeDefined();
      expect(getUserMethod?.signature).toContain('func (s *UserService) GetUser(id int64) Result[*User]');

      const handleError = symbols.find(s => s.name === 'HandleError');
      expect(handleError).toBeDefined();
      expect(handleError?.signature).toContain('func HandleError(err error)');

      const safeOperation = symbols.find(s => s.name === 'SafeOperation');
      expect(safeOperation).toBeDefined();
      expect(safeOperation?.signature).toContain('func SafeOperation() (result string, err error)');
    });
  });

  describe('Interfaces and Type Assertions', () => {
    it('should extract interfaces, type assertions, and type switches', async () => {
      const goCode = `
package main

import (
    "fmt"
    "reflect"
)

// Basic interfaces
type Reader interface {
    Read([]byte) (int, error)
}

type Writer interface {
    Write([]byte) (int, error)
}

type Closer interface {
    Close() error
}

// Composed interface
type ReadWriteCloser interface {
    Reader
    Writer
    Closer
}

// Interface with type constraints
type Stringer interface {
    String() string
}

// Empty interface usage
type Container struct {
    Value interface{}
}

func (c *Container) Set(value interface{}) {
    c.Value = value
}

func (c *Container) Get() interface{} {
    return c.Value
}

func (c *Container) GetString() (string, bool) {
    if str, ok := c.Value.(string); ok {
        return str, true
    }
    return "", false
}

func (c *Container) GetInt() (int, bool) {
    if i, ok := c.Value.(int); ok {
        return i, true
    }
    return 0, false
}

// Type assertion and type switches
func ProcessValue(value interface{}) string {
    switch v := value.(type) {
    case string:
        return fmt.Sprintf("String: %s", v)
    case int:
        return fmt.Sprintf("Integer: %d", v)
    case float64:
        return fmt.Sprintf("Float: %.2f", v)
    case bool:
        return fmt.Sprintf("Boolean: %t", v)
    case nil:
        return "Nil value"
    case Stringer:
        return fmt.Sprintf("Stringer: %s", v.String())
    default:
        return fmt.Sprintf("Unknown type: %T", v)
    }
}

// Interface satisfaction checking
func CheckInterface(value interface{}) {
    if reader, ok := value.(Reader); ok {
        fmt.Println("Value implements Reader")
        buffer := make([]byte, 10)
        reader.Read(buffer)
    }

    if writer, ok := value.(Writer); ok {
        fmt.Println("Value implements Writer")
        writer.Write([]byte("hello"))
    }

    if rw, ok := value.(ReadWriteCloser); ok {
        fmt.Println("Value implements ReadWriteCloser")
        defer rw.Close()
    }
}

// Dynamic type checking
func InspectType(value interface{}) {
    t := reflect.TypeOf(value)
    v := reflect.ValueOf(value)

    fmt.Printf("Type: %s\n", t)
    fmt.Printf("Kind: %s\n", t.Kind())
    fmt.Printf("Value: %v\n", v)

    if t.Kind() == reflect.Struct {
        for i := 0; i < t.NumField(); i++ {
            field := t.Field(i)
            fieldValue := v.Field(i)
            fmt.Printf("Field %s: %v\n", field.Name, fieldValue)
        }
    }
}

// Interface implementation examples
type FileReader struct {
    filename string
}

func (f *FileReader) Read(data []byte) (int, error) {
    // Read from file
    return len(data), nil
}

func (f *FileReader) Close() error {
    // Close file
    return nil
}

type MemoryWriter struct {
    buffer []byte
}

func (m *MemoryWriter) Write(data []byte) (int, error) {
    m.buffer = append(m.buffer, data...)
    return len(data), nil
}

func (m *MemoryWriter) Close() error {
    m.buffer = nil
    return nil
}

// Function that accepts interface
func Copy(reader Reader, writer Writer) error {
    buffer := make([]byte, 1024)

    for {
        n, err := reader.Read(buffer)
        if err != nil {
            return err
        }

        if n == 0 {
            break
        }

        _, err = writer.Write(buffer[:n])
        if err != nil {
            return err
        }
    }

    return nil
}

// Interface as return type
func CreateReader(source string) Reader {
    return &FileReader{filename: source}
}

func CreateWriter() Writer {
    return &MemoryWriter{}
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const reader = symbols.find(s => s.name === 'Reader');
      expect(reader).toBeDefined();
      expect(reader?.kind).toBe(SymbolKind.Interface);
      expect(reader?.signature).toContain('type Reader interface');

      const writer = symbols.find(s => s.name === 'Writer');
      expect(writer).toBeDefined();
      expect(writer?.kind).toBe(SymbolKind.Interface);

      const closer = symbols.find(s => s.name === 'Closer');
      expect(closer).toBeDefined();
      expect(closer?.kind).toBe(SymbolKind.Interface);

      const readWriteCloser = symbols.find(s => s.name === 'ReadWriteCloser');
      expect(readWriteCloser).toBeDefined();
      expect(readWriteCloser?.kind).toBe(SymbolKind.Interface);
      expect(readWriteCloser?.signature).toContain('type ReadWriteCloser interface');

      const container = symbols.find(s => s.name === 'Container');
      expect(container).toBeDefined();
      expect(container?.kind).toBe(SymbolKind.Class);
      expect(container?.signature).toContain('type Container struct');

      const processValue = symbols.find(s => s.name === 'ProcessValue');
      expect(processValue).toBeDefined();
      expect(processValue?.signature).toContain('func ProcessValue(value interface{}) string');

      const checkInterface = symbols.find(s => s.name === 'CheckInterface');
      expect(checkInterface).toBeDefined();
      expect(checkInterface?.signature).toContain('func CheckInterface(value interface{})');

      const inspectType = symbols.find(s => s.name === 'InspectType');
      expect(inspectType).toBeDefined();
      expect(inspectType?.signature).toContain('func InspectType(value interface{})');

      const fileReader = symbols.find(s => s.name === 'FileReader');
      expect(fileReader).toBeDefined();
      expect(fileReader?.kind).toBe(SymbolKind.Class);

      const memoryWriter = symbols.find(s => s.name === 'MemoryWriter');
      expect(memoryWriter).toBeDefined();
      expect(memoryWriter?.kind).toBe(SymbolKind.Class);

      const copyFunc = symbols.find(s => s.name === 'Copy');
      expect(copyFunc).toBeDefined();
      expect(copyFunc?.signature).toContain('func Copy(reader Reader, writer Writer) error');
    });
  });

  describe('Testing Patterns and Build Tags', () => {
    it('should extract test functions and build constraints', async () => {
      const goCode = `
// +build integration
// +build !race

package main

import (
    "testing"
    "time"
)

// Test function
func TestUserService(t *testing.T) {
    service := NewUserService()

    user := &User{Name: "John Doe"}
    err := service.CreateUser(user)
    if err != nil {
        t.Errorf("CreateUser failed: %v", err)
    }

    retrievedUser, err := service.GetUser(user.ID)
    if err != nil {
        t.Errorf("GetUser failed: %v", err)
    }

    if retrievedUser.Name != user.Name {
        t.Errorf("Expected name %s, got %s", user.Name, retrievedUser.Name)
    }
}

// Table-driven test
func TestValidation(t *testing.T) {
    tests := []struct {
        name     string
        input    string
        expected bool
    }{
        {"valid email", "test@example.com", true},
        {"invalid email", "invalid", false},
        {"empty string", "", false},
    }

    for _, tt := range tests {
        t.Run(tt.name, func(t *testing.T) {
            result := IsValidEmail(tt.input)
            if result != tt.expected {
                t.Errorf("IsValidEmail(%s) = %v, want %v", tt.input, result, tt.expected)
            }
        })
    }
}

// Benchmark function
func BenchmarkUserCreation(b *testing.B) {
    service := NewUserService()

    b.ResetTimer()
    for i := 0; i < b.N; i++ {
        user := &User{Name: fmt.Sprintf("User %d", i)}
        service.CreateUser(user)
    }
}

// Benchmark with sub-benchmarks
func BenchmarkHashFunctions(b *testing.B) {
    data := []byte("hello world")

    b.Run("MD5", func(b *testing.B) {
        for i := 0; i < b.N; i++ {
            md5.Sum(data)
        }
    })

    b.Run("SHA256", func(b *testing.B) {
        for i := 0; i < b.N; i++ {
            sha256.Sum256(data)
        }
    })
}

// Test with setup and teardown
func TestDatabaseOperations(t *testing.T) {
    // Setup
    db := setupTestDatabase(t)
    defer teardownTestDatabase(t, db)

    // Test cases
    t.Run("Insert", func(t *testing.T) {
        err := db.Insert("test data")
        if err != nil {
            t.Fatalf("Insert failed: %v", err)
        }
    })

    t.Run("Select", func(t *testing.T) {
        data, err := db.Select("test data")
        if err != nil {
            t.Fatalf("Select failed: %v", err)
        }
        if data == nil {
            t.Error("Expected data, got nil")
        }
    })
}

// Helper functions
func setupTestDatabase(t *testing.T) *Database {
    t.Helper()
    db, err := NewDatabase(":memory:")
    if err != nil {
        t.Fatalf("Failed to create test database: %v", err)
    }
    return db
}

func teardownTestDatabase(t *testing.T, db *Database) {
    t.Helper()
    if err := db.Close(); err != nil {
        t.Errorf("Failed to close database: %v", err)
    }
}

// Example function
func ExampleUserService_CreateUser() {
    service := NewUserService()
    user := &User{Name: "John Doe"}

    err := service.CreateUser(user)
    if err != nil {
        fmt.Printf("Error: %v", err)
        return
    }

    fmt.Printf("User created with ID: %d", user.ID)
    // Output: User created with ID: 1
}

// Fuzzing test (Go 1.18+)
func FuzzUserValidation(f *testing.F) {
    f.Add("test@example.com")
    f.Add("invalid")
    f.Add("")

    f.Fuzz(func(t *testing.T, email string) {
        result := IsValidEmail(email)
        // Test that the function doesn't panic
        _ = result
    })
}

// Platform-specific functions
// +build linux
func getLinuxSpecificInfo() string {
    return "Linux-specific information"
}

// +build windows
func getWindowsSpecificInfo() string {
    return "Windows-specific information"
}

// +build cgo
func useCGO() {
    // CGO-enabled code
}

// +build !cgo
func noCGO() {
    // Pure Go code
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      const testUserService = symbols.find(s => s.name === 'TestUserService');
      expect(testUserService).toBeDefined();
      expect(testUserService?.kind).toBe(SymbolKind.Function);
      expect(testUserService?.signature).toContain('func TestUserService(t *testing.T)');

      const testValidation = symbols.find(s => s.name === 'TestValidation');
      expect(testValidation).toBeDefined();
      expect(testValidation?.signature).toContain('func TestValidation(t *testing.T)');

      const benchmarkUserCreation = symbols.find(s => s.name === 'BenchmarkUserCreation');
      expect(benchmarkUserCreation).toBeDefined();
      expect(benchmarkUserCreation?.signature).toContain('func BenchmarkUserCreation(b *testing.B)');

      const benchmarkHashFunctions = symbols.find(s => s.name === 'BenchmarkHashFunctions');
      expect(benchmarkHashFunctions).toBeDefined();
      expect(benchmarkHashFunctions?.signature).toContain('func BenchmarkHashFunctions(b *testing.B)');

      const testDatabaseOps = symbols.find(s => s.name === 'TestDatabaseOperations');
      expect(testDatabaseOps).toBeDefined();
      expect(testDatabaseOps?.signature).toContain('func TestDatabaseOperations(t *testing.T)');

      const setupTestDB = symbols.find(s => s.name === 'setupTestDatabase');
      expect(setupTestDB).toBeDefined();
      expect(setupTestDB?.signature).toContain('func setupTestDatabase(t *testing.T) *Database');

      const teardownTestDB = symbols.find(s => s.name === 'teardownTestDatabase');
      expect(teardownTestDB).toBeDefined();
      expect(teardownTestDB?.signature).toContain('func teardownTestDatabase(t *testing.T, db *Database)');

      const exampleFunc = symbols.find(s => s.name === 'ExampleUserService_CreateUser');
      expect(exampleFunc).toBeDefined();
      expect(exampleFunc?.signature).toContain('func ExampleUserService_CreateUser()');

      const fuzzFunc = symbols.find(s => s.name === 'FuzzUserValidation');
      expect(fuzzFunc).toBeDefined();
      expect(fuzzFunc?.signature).toContain('func FuzzUserValidation(f *testing.F)');

      const linuxFunc = symbols.find(s => s.name === 'getLinuxSpecificInfo');
      expect(linuxFunc).toBeDefined();
      expect(linuxFunc?.signature).toContain('func getLinuxSpecificInfo() string');

      const cgoFunc = symbols.find(s => s.name === 'useCGO');
      expect(cgoFunc).toBeDefined();
      expect(cgoFunc?.signature).toContain('func useCGO()');
    });
  });

  describe('Complex Go Features', () => {
    it('should handle comprehensive Go code', async () => {
      const goCode = `
package main

import (
    "context"
    "fmt"
    "net/http"
)

// User represents a user in the system
type User struct {
    ID       int64  \`json:"id"\`
    Name     string \`json:"name"\`
    Email    string \`json:"email,omitempty"\`
}

// UserService interface for user operations
type UserService interface {
    GetUser(ctx context.Context, id int64) (*User, error)
    CreateUser(ctx context.Context, user *User) error
}

// UserRepository implements UserService
type UserRepository struct {
    db *sql.DB
}

// GetUser retrieves a user by ID
func (r *UserRepository) GetUser(ctx context.Context, id int64) (*User, error) {
    // Implementation here
    return nil, nil
}

// CreateUser creates a new user
func (r *UserRepository) CreateUser(ctx context.Context, user *User) error {
    // Implementation here
    return nil
}

// ProcessUsers processes users concurrently
func ProcessUsers(users []User) <-chan User {
    resultCh := make(chan User, len(users))

    go func() {
        defer close(resultCh)
        for _, user := range users {
            resultCh <- user
        }
    }()

    return resultCh
}

const (
    MaxUsers = 1000
    DefaultTimeout = 30
)

var (
    GlobalConfig *Config
    Logger       *log.Logger
)

func main() {
    http.HandleFunc("/users", handleUsers)
    fmt.Println("Server starting")
    http.ListenAndServe(":8080", nil)
}

func handleUsers(w http.ResponseWriter, r *http.Request) {
    // HTTP handler implementation
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Check we extracted all major symbols
      expect(symbols.find(s => s.name === 'main')).toBeDefined();
      expect(symbols.find(s => s.name === 'User')).toBeDefined();
      expect(symbols.find(s => s.name === 'UserService')).toBeDefined();
      expect(symbols.find(s => s.name === 'UserRepository')).toBeDefined();
      expect(symbols.find(s => s.name === 'GetUser')).toBeDefined();
      expect(symbols.find(s => s.name === 'CreateUser')).toBeDefined();
      expect(symbols.find(s => s.name === 'ProcessUsers')).toBeDefined();
      expect(symbols.find(s => s.name === 'MaxUsers')).toBeDefined();
      expect(symbols.find(s => s.name === 'GlobalConfig')).toBeDefined();
      expect(symbols.find(s => s.name === 'handleUsers')).toBeDefined();

      // Check specific features
      const userStruct = symbols.find(s => s.name === 'User');
      expect(userStruct?.kind).toBe(SymbolKind.Class);

      const userService = symbols.find(s => s.name === 'UserService');
      expect(userService?.kind).toBe(SymbolKind.Interface);

      const getUser = symbols.find(s => s.name === 'GetUser');
      expect(getUser?.kind).toBe(SymbolKind.Method);
      expect(getUser?.signature).toContain('func (r *UserRepository) GetUser');

      const processUsers = symbols.find(s => s.name === 'ProcessUsers');
      expect(processUsers?.signature).toContain('<-chan User');

      console.log(`🐹 Extracted ${symbols.length} Go symbols successfully`);
    });
  });

  describe('Performance and Edge Cases', () => {
    it('should handle large Go files with many symbols', async () => {
      // Generate a large Go file with many types and functions
      const types = Array.from({ length: 20 }, (_, i) => `
// Service${i} represents service ${i}
type Service${i} struct {
    ID     int64
    Name   string
    Config map[string]interface{}
    Active bool
}

func (s *Service${i}) Start() error {
    s.Active = true
    return nil
}

func (s *Service${i}) Stop() error {
    s.Active = false
    return nil
}

func (s *Service${i}) GetStatus() string {
    if s.Active {
        return "running"
    }
    return "stopped"
}

func NewService${i}(name string) *Service${i} {
    return &Service${i}{
        ID:     ${i},
        Name:   name,
        Config: make(map[string]interface{}),
        Active: false,
    }
}`).join('\n');

      const interfaces = Array.from({ length: 5 }, (_, i) => `
type Interface${i} interface {
    Method${i}A() error
    Method${i}B(param string) (string, error)
    Method${i}C(ctx context.Context) <-chan Result
}`).join('\n');

      const functions = Array.from({ length: 10 }, (_, i) => `
func GlobalFunction${i}(param1 string, param2 int) (string, error) {
    return fmt.Sprintf("function%d: %s-%d", ${i}, param1, param2), nil
}

func ProcessData${i}(data []byte) <-chan ProcessedData {
    ch := make(chan ProcessedData)
    go func() {
        defer close(ch)
        // Process data
        result := ProcessedData{ID: ${i}, Data: data}
        ch <- result
    }()
    return ch
}`).join('\n');

      const goCode = `
package main

import (
    "context"
    "fmt"
    "sync"
    "time"
)

// Constants
const (
    MaxConnections = 1000
    DefaultTimeout = 30 * time.Second
    Version        = "1.0.0"
    BuildDate      = "2023-01-01"
)

// Global variables
var (
    GlobalCounter int64
    GlobalMutex   sync.RWMutex
    GlobalConfig  map[string]interface{}
    Logger        *CustomLogger
)

// Common types
type ProcessedData struct {
    ID   int
    Data []byte
}

type Result struct {
    Success bool
    Message string
    Data    interface{}
}

${types}
${interfaces}
${functions}

// Manager struct that uses all services
type ServiceManager struct {
    services map[string]interface{}
    mu       sync.RWMutex
    ctx      context.Context
    cancel   context.CancelFunc
}

func NewServiceManager() *ServiceManager {
    ctx, cancel := context.WithCancel(context.Background())
    return &ServiceManager{
        services: make(map[string]interface{}),
        ctx:      ctx,
        cancel:   cancel,
    }
}

func (sm *ServiceManager) RegisterService(name string, service interface{}) {
    sm.mu.Lock()
    defer sm.mu.Unlock()
    sm.services[name] = service
}

func (sm *ServiceManager) StartAll() error {
    sm.mu.RLock()
    defer sm.mu.RUnlock()

    for name, service := range sm.services {
        if starter, ok := service.(interface{ Start() error }); ok {
            if err := starter.Start(); err != nil {
                return fmt.Errorf("failed to start service %s: %w", name, err)
            }
        }
    }

    return nil
}

func (sm *ServiceManager) Shutdown() {
    sm.cancel()

    sm.mu.RLock()
    defer sm.mu.RUnlock()

    for _, service := range sm.services {
        if stopper, ok := service.(interface{ Stop() error }); ok {
            stopper.Stop()
        }
    }
}

func main() {
    manager := NewServiceManager()

    // Register all services
    for i := 0; i < 20; i++ {
        serviceName := fmt.Sprintf("service%d", i)
        switch i % 4 {
        case 0:
            manager.RegisterService(serviceName, NewService0(serviceName))
        case 1:
            manager.RegisterService(serviceName, NewService1(serviceName))
        case 2:
            manager.RegisterService(serviceName, NewService2(serviceName))
        case 3:
            manager.RegisterService(serviceName, NewService3(serviceName))
        }
    }

    if err := manager.StartAll(); err != nil {
        panic(err)
    }

    fmt.Println("All services started successfully")

    // Graceful shutdown
    defer manager.Shutdown()
}
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should extract many symbols
      expect(symbols.length).toBeGreaterThan(150);

      // Check that all generated services were extracted
      for (let i = 0; i < 20; i++) {
        const service = symbols.find(s => s.name === `Service${i}`);
        expect(service).toBeDefined();
        expect(service?.kind).toBe(SymbolKind.Class);
      }

      // Check that all interfaces were extracted
      for (let i = 0; i < 5; i++) {
        const iface = symbols.find(s => s.name === `Interface${i}`);
        expect(iface).toBeDefined();
        expect(iface?.kind).toBe(SymbolKind.Interface);
      }

      // Check that all functions were extracted
      for (let i = 0; i < 10; i++) {
        const func = symbols.find(s => s.name === `GlobalFunction${i}`);
        expect(func).toBeDefined();
        expect(func?.kind).toBe(SymbolKind.Function);
      }

      // Check service manager
      const manager = symbols.find(s => s.name === 'ServiceManager');
      expect(manager).toBeDefined();
      expect(manager?.kind).toBe(SymbolKind.Class);

      // Check constants
      const maxConnections = symbols.find(s => s.name === 'MaxConnections');
      expect(maxConnections).toBeDefined();
      expect(maxConnections?.kind).toBe(SymbolKind.Constant);

      const version = symbols.find(s => s.name === 'Version');
      expect(version).toBeDefined();
      expect(version?.kind).toBe(SymbolKind.Constant);

      console.log(`🐹 Performance test: Extracted ${symbols.length} symbols and ${relationships.length} relationships`);
    });

    it('should handle edge cases and malformed code gracefully', async () => {
      const goCode = `
package main

// Edge cases and unusual Go constructs

// Empty struct
type Empty struct{}

// Struct with embedded types
type EmbeddedStruct struct {
    Empty
    *User
    io.Reader
    value int
}

// Interface with embedded interfaces
type ComplexInterface interface {
    io.Reader
    io.Writer
    fmt.Stringer
    CustomMethod() error
}

// Function with complex signature
func ComplexFunction(
    ctx context.Context,
    args ...interface{},
) (result chan<- string, cleanup func() error, err error) {
    return nil, nil, nil
}

// Function with named return values
func NamedReturns(x, y int) (sum, product int) {
    sum = x + y
    product = x * y
    return // naked return
}

// Malformed code that shouldn't crash parser
type MissingBrace struct {
    field int
// Missing closing brace

// Variadic function
func VariadicFunction(format string, args ...interface{}) {
    fmt.Printf(format, args...)
}

// Function type
type HandlerFunc func(http.ResponseWriter, *http.Request)

// Channel types
type Channels struct {
    input    <-chan string
    output   chan<- int
    bidirect chan bool
}

// Map and slice types
type Collections struct {
    stringMap    map[string]int
    interfaceMap map[interface{}]interface{}
    intSlice     []int
    structSlice  []struct{ Name string }
}

// Pointer types
type Pointers struct {
    intPtr    *int
    structPtr *User
    funcPtr   *func() error
}

// Anonymous struct field
type WithAnonymous struct {
    struct {
        NestedField string
    }
    RegularField int
}

// Method with pointer receiver vs value receiver
func (e Empty) ValueMethod() {}
func (e *Empty) PointerMethod() {}

// Interface method with no parameters
type SimpleInterface interface {
    NoParams()
    WithParams(string, int) error
    WithReturn() string
}

// Const with iota
const (
    FirstValue = iota
    SecondValue
    ThirdValue
    _  // skip a value
    FifthValue
)

// Type alias vs type definition
type TypeAlias = string
type TypeDefinition string

// Package-level function with init
func init() {
    // Initialization code
}

// Multiple variable declarations
var a, b, c int
var (
    x = 1
    y = 2
    z string
)
`;

      const result = await parserManager.parseFile('test.go', goCode);
      const extractor = new GoExtractor('go', 'test.go', goCode);

      // Should not throw even with malformed code
      expect(() => {
        const symbols = extractor.extractSymbols(result.tree);
        const relationships = extractor.extractRelationships(result.tree, symbols);
      }).not.toThrow();

      const symbols = extractor.extractSymbols(result.tree);

      // Should still extract valid symbols
      const empty = symbols.find(s => s.name === 'Empty');
      expect(empty).toBeDefined();
      expect(empty?.kind).toBe(SymbolKind.Class);

      const embeddedStruct = symbols.find(s => s.name === 'EmbeddedStruct');
      expect(embeddedStruct).toBeDefined();
      expect(embeddedStruct?.kind).toBe(SymbolKind.Class);

      const complexInterface = symbols.find(s => s.name === 'ComplexInterface');
      expect(complexInterface).toBeDefined();
      expect(complexInterface?.kind).toBe(SymbolKind.Interface);

      const complexFunction = symbols.find(s => s.name === 'ComplexFunction');
      expect(complexFunction).toBeDefined();
      expect(complexFunction?.signature).toContain('func ComplexFunction');

      const namedReturns = symbols.find(s => s.name === 'NamedReturns');
      expect(namedReturns).toBeDefined();
      expect(namedReturns?.signature).toContain('(sum, product int)');

      const variadicFunc = symbols.find(s => s.name === 'VariadicFunction');
      expect(variadicFunc).toBeDefined();
      expect(variadicFunc?.signature).toContain('args ...interface{}');

      const handlerFunc = symbols.find(s => s.name === 'HandlerFunc');
      expect(handlerFunc).toBeDefined();
      expect(handlerFunc?.kind).toBe(SymbolKind.Type);

      const channels = symbols.find(s => s.name === 'Channels');
      expect(channels).toBeDefined();
      expect(channels?.kind).toBe(SymbolKind.Class);

      const typeAlias = symbols.find(s => s.name === 'TypeAlias');
      expect(typeAlias).toBeDefined();
      expect(typeAlias?.kind).toBe(SymbolKind.Type);

      const typeDefinition = symbols.find(s => s.name === 'TypeDefinition');
      expect(typeDefinition).toBeDefined();
      expect(typeDefinition?.kind).toBe(SymbolKind.Type);

      const initFunc = symbols.find(s => s.name === 'init');
      expect(initFunc).toBeDefined();
      expect(initFunc?.kind).toBe(SymbolKind.Function);

      console.log(`🐹 Edge case test: Extracted ${symbols.length} symbols from complex code`);
    });
  });
});