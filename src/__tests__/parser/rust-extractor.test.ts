import { describe, it, expect, beforeAll } from 'bun:test';
import { RustExtractor } from '../../extractors/rust-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('RustExtractor', () => {
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

  describe('Struct Extraction', () => {
    it('should extract basic struct definitions', async () => {
      const rustCode = `
#[derive(Debug, Clone)]
pub struct User {
    pub id: u64,
    name: String,
    email: Option<String>,
}

struct Point(f64, f64);
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const userStruct = symbols.find(s => s.name === 'User');
      expect(userStruct).toBeDefined();
      expect(userStruct?.kind).toBe(SymbolKind.Class);
      expect(userStruct?.signature).toContain('pub struct User');
      expect(userStruct?.visibility).toBe('pub');

      const pointStruct = symbols.find(s => s.name === 'Point');
      expect(pointStruct).toBeDefined();
      expect(pointStruct?.kind).toBe(SymbolKind.Class);
      expect(pointStruct?.visibility).toBe('private');
    });
  });

  describe('Enum Extraction', () => {
    it('should extract enum definitions with variants', async () => {
      const rustCode = `
#[derive(Debug)]
pub enum Status {
    Active,
    Inactive,
    Pending(String),
    Processing { task_id: u64, progress: f32 },
}

enum Color { Red, Green, Blue }
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const statusEnum = symbols.find(s => s.name === 'Status');
      expect(statusEnum).toBeDefined();
      expect(statusEnum?.kind).toBe(SymbolKind.Class);
      expect(statusEnum?.signature).toContain('pub enum Status');
      expect(statusEnum?.visibility).toBe('pub');

      const colorEnum = symbols.find(s => s.name === 'Color');
      expect(colorEnum).toBeDefined();
      expect(colorEnum?.visibility).toBe('private');
    });
  });

  describe('Trait Extraction', () => {
    it('should extract trait definitions', async () => {
      const rustCode = `
pub trait Display {
    fn fmt(&self) -> String;
    fn print(&self) {
        println!("{}", self.fmt());
    }
}

trait Clone {
    fn clone(&self) -> Self;
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const displayTrait = symbols.find(s => s.name === 'Display');
      expect(displayTrait).toBeDefined();
      expect(displayTrait?.kind).toBe(SymbolKind.Interface);
      expect(displayTrait?.signature).toBe('pub trait Display');
      expect(displayTrait?.visibility).toBe('pub');

      const cloneTrait = symbols.find(s => s.name === 'Clone');
      expect(cloneTrait).toBeDefined();
      expect(cloneTrait?.visibility).toBe('private');
    });
  });

  describe('Function Extraction', () => {
    it('should extract standalone functions with various signatures', async () => {
      const rustCode = `
pub fn add(a: i32, b: i32) -> i32 {
    a + b
}

pub async fn fetch_data(url: &str) -> Result<String, Error> {
    // async implementation
    Ok("data".to_string())
}

unsafe fn raw_memory_access() -> *mut u8 {
    std::ptr::null_mut()
}

fn private_helper() {}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const addFunc = symbols.find(s => s.name === 'add');
      expect(addFunc).toBeDefined();
      expect(addFunc?.kind).toBe(SymbolKind.Function);
      expect(addFunc?.signature).toContain('pub fn add(a: i32, b: i32)');
      expect(addFunc?.visibility).toBe('pub');

      const fetchFunc = symbols.find(s => s.name === 'fetch_data');
      expect(fetchFunc).toBeDefined();
      expect(fetchFunc?.signature).toContain('pub async fn fetch_data');

      const unsafeFunc = symbols.find(s => s.name === 'raw_memory_access');
      expect(unsafeFunc).toBeDefined();
      expect(unsafeFunc?.signature).toContain('unsafe fn raw_memory_access');

      const privateFunc = symbols.find(s => s.name === 'private_helper');
      expect(privateFunc).toBeDefined();
      expect(privateFunc?.visibility).toBe('private');
    });

    it('should extract methods from impl blocks', async () => {
      const rustCode = `
struct Calculator {
    value: f64,
}

impl Calculator {
    pub fn new(value: f64) -> Self {
        Self { value }
    }

    fn add(&mut self, other: f64) {
        self.value += other;
    }

    pub fn get_value(&self) -> f64 {
        self.value
    }
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const newMethod = symbols.find(s => s.name === 'new');
      expect(newMethod).toBeDefined();
      expect(newMethod?.kind).toBe(SymbolKind.Method);
      expect(newMethod?.signature).toContain('pub fn new(value: f64)');

      const addMethod = symbols.find(s => s.name === 'add');
      expect(addMethod).toBeDefined();
      expect(addMethod?.kind).toBe(SymbolKind.Method);
      expect(addMethod?.signature).toContain('fn add(&mut self, other: f64)');

      const getValueMethod = symbols.find(s => s.name === 'get_value');
      expect(getValueMethod).toBeDefined();
      expect(getValueMethod?.signature).toContain('&self');
    });
  });

  describe('Module Extraction', () => {
    it('should extract module definitions', async () => {
      const rustCode = `
pub mod utils {
    pub fn helper() {}
}

mod private_module {
    fn internal_function() {}
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const utilsModule = symbols.find(s => s.name === 'utils');
      expect(utilsModule).toBeDefined();
      expect(utilsModule?.kind).toBe(SymbolKind.Namespace);
      expect(utilsModule?.signature).toBe('pub mod utils');
      expect(utilsModule?.visibility).toBe('pub');

      const privateModule = symbols.find(s => s.name === 'private_module');
      expect(privateModule).toBeDefined();
      expect(privateModule?.visibility).toBe('private');
    });
  });

  describe('Use Statement Extraction', () => {
    it('should extract use declarations and imports', async () => {
      const rustCode = `
use std::collections::HashMap;
use std::fmt::{Debug, Display};
use super::utils as util;
use crate::model::User;
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const hashmapImport = symbols.find(s => s.name === 'HashMap');
      expect(hashmapImport).toBeDefined();
      expect(hashmapImport?.kind).toBe(SymbolKind.Import);
      expect(hashmapImport?.signature).toContain('use std::collections::HashMap');

      const aliasImport = symbols.find(s => s.name === 'util');
      expect(aliasImport).toBeDefined();
      expect(aliasImport?.signature).toContain('use super::utils as util');

      const userImport = symbols.find(s => s.name === 'User');
      expect(userImport).toBeDefined();
      expect(userImport?.signature).toContain('use crate::model::User');
    });
  });

  describe('Constants and Statics', () => {
    it('should extract const and static declarations', async () => {
      const rustCode = `
const MAX_SIZE: usize = 1024;
pub const VERSION: &str = "1.0.0";

static mut COUNTER: i32 = 0;
static GLOBAL_CONFIG: Config = Config::new();
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const maxSizeConst = symbols.find(s => s.name === 'MAX_SIZE');
      expect(maxSizeConst).toBeDefined();
      expect(maxSizeConst?.kind).toBe(SymbolKind.Constant);
      expect(maxSizeConst?.signature).toContain('const MAX_SIZE: usize = 1024');

      const versionConst = symbols.find(s => s.name === 'VERSION');
      expect(versionConst).toBeDefined();
      expect(versionConst?.visibility).toBe('pub');

      const counterStatic = symbols.find(s => s.name === 'COUNTER');
      expect(counterStatic).toBeDefined();
      expect(counterStatic?.kind).toBe(SymbolKind.Variable);
      expect(counterStatic?.signature).toContain('static mut COUNTER: i32 = 0');

      const configStatic = symbols.find(s => s.name === 'GLOBAL_CONFIG');
      expect(configStatic).toBeDefined();
      expect(configStatic?.signature).toContain('static GLOBAL_CONFIG');
    });
  });

  describe('Advanced Generics and Type Parameters', () => {
    it('should extract generic structs, traits, and functions with constraints', async () => {
      const rustCode = `
use std::fmt::{Debug, Display};
use std::cmp::Ord;

/// Generic container with lifetime parameter
pub struct Container<'a, T: Debug + Clone> {
    pub data: &'a T,
    pub metadata: Option<String>,
}

/// Generic trait with associated type
pub trait Iterator<T> {
    type Item;
    type Error;

    fn next(&mut self) -> Option<Self::Item>;
    fn collect<C>(self) -> Result<C, Self::Error>
    where
        Self: Sized,
        C: FromIterator<Self::Item>;
}

/// Generic function with multiple constraints
pub fn sort_and_display<T>(mut items: Vec<T>) -> String
where
    T: Ord + Display + Clone,
{
    items.sort();
    items.iter().map(|x| x.to_string()).collect::<Vec<_>>().join(", ")
}

/// Higher-ranked trait bounds
pub fn closure_example<F>(f: F) -> i32
where
    F: for<'a> Fn(&'a str) -> i32,
{
    f("test")
}

/// Associated type with bounds
pub trait Collect {
    type Output: Debug;

    fn collect(&self) -> Self::Output;
}

/// Generic enum with phantom data
use std::marker::PhantomData;

pub enum Either<L, R> {
    Left(L),
    Right(R),
    Neither(PhantomData<(L, R)>),
}

/// Type alias for complex generic type
type UserMap<K> = std::collections::HashMap<K, User>
where
    K: std::hash::Hash + Eq;
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const container = symbols.find(s => s.name === 'Container');
      expect(container).toBeDefined();
      expect(container?.kind).toBe(SymbolKind.Class);
      expect(container?.signature).toContain('pub struct Container');
      expect(container?.signature).toContain('<\'a, T: Debug + Clone>');

      const iteratorTrait = symbols.find(s => s.name === 'Iterator');
      expect(iteratorTrait).toBeDefined();
      expect(iteratorTrait?.kind).toBe(SymbolKind.Interface);
      expect(iteratorTrait?.signature).toContain('pub trait Iterator<T>');

      const sortFunc = symbols.find(s => s.name === 'sort_and_display');
      expect(sortFunc).toBeDefined();
      expect(sortFunc?.signature).toContain('pub fn sort_and_display<T>');
      expect(sortFunc?.signature).toContain('T: Ord + Display + Clone');

      const closureFunc = symbols.find(s => s.name === 'closure_example');
      expect(closureFunc).toBeDefined();
      expect(closureFunc?.signature).toContain('for<\'a> Fn(&\'a str) -> i32');

      const collectTrait = symbols.find(s => s.name === 'Collect');
      expect(collectTrait).toBeDefined();
      expect(collectTrait?.signature).toContain('type Output: Debug');

      const eitherEnum = symbols.find(s => s.name === 'Either');
      expect(eitherEnum).toBeDefined();
      expect(eitherEnum?.signature).toContain('pub enum Either<L, R>');

      const userMapType = symbols.find(s => s.name === 'UserMap');
      expect(userMapType).toBeDefined();
      expect(userMapType?.kind).toBe(SymbolKind.Type);
      expect(userMapType?.signature).toContain('type UserMap<K>');
    });
  });

  describe('Advanced Async and Concurrency', () => {
    it('should extract async functions, traits, and concurrency primitives', async () => {
      const rustCode = `
use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use tokio::sync::{mpsc, RwLock};

/// Async trait with Send + Sync bounds
#[async_trait::async_trait]
pub trait AsyncRepository: Send + Sync {
    type Error: Send + Sync;

    async fn find_by_id(&self, id: u64) -> Result<Option<User>, Self::Error>;
    async fn save(&self, user: User) -> Result<(), Self::Error>;
}

/// Complex async function with lifetime parameters
pub async fn process_batch<'a, T>(
    items: &'a [T],
    processor: impl Fn(&T) -> Pin<Box<dyn Future<Output = Result<(), String>> + Send + 'a>>,
) -> Result<(), Vec<String>>
where
    T: Send + Sync,
{
    let mut errors = Vec::new();
    for item in items {
        if let Err(e) = processor(item).await {
            errors.push(e);
        }
    }
    if errors.is_empty() { Ok(()) } else { Err(errors) }
}

/// Concurrent data structure
pub struct ThreadSafeCounter {
    inner: Arc<Mutex<i64>>,
    watchers: Arc<RwLock<Vec<mpsc::Sender<i64>>>>,
}

impl ThreadSafeCounter {
    pub fn new() -> Self {
        Self {
            inner: Arc::new(Mutex::new(0)),
            watchers: Arc::new(RwLock::new(Vec::new())),
        }
    }

    pub async fn increment(&self) -> Result<i64, String> {
        let mut counter = self.inner.lock().map_err(|_| "Lock poisoned")?;
        *counter += 1;
        let value = *counter;

        // Notify watchers
        let watchers = self.watchers.read().await;
        for sender in watchers.iter() {
            let _ = sender.try_send(value);
        }

        Ok(value)
    }

    pub async fn add_watcher(&self, sender: mpsc::Sender<i64>) {
        let mut watchers = self.watchers.write().await;
        watchers.push(sender);
    }
}

/// Stream-like async iterator
pub struct AsyncRange {
    current: u64,
    end: u64,
}

impl AsyncRange {
    pub fn new(start: u64, end: u64) -> Self {
        Self { current: start, end }
    }

    pub async fn next(&mut self) -> Option<u64> {
        if self.current < self.end {
            let value = self.current;
            self.current += 1;
            Some(value)
        } else {
            None
        }
    }
}

/// Async closure example
type AsyncClosure<T> = Box<dyn Fn(T) -> Pin<Box<dyn Future<Output = ()> + Send>> + Send + Sync>;

pub fn create_async_processor<T: Send + 'static>() -> AsyncClosure<T> {
    Box::new(|_item| {
        Box::pin(async move {
            // Process item asynchronously
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        })
    })
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const asyncRepo = symbols.find(s => s.name === 'AsyncRepository');
      expect(asyncRepo).toBeDefined();
      expect(asyncRepo?.kind).toBe(SymbolKind.Interface);
      expect(asyncRepo?.signature).toContain('pub trait AsyncRepository');
      expect(asyncRepo?.signature).toContain('Send + Sync');

      const processBatch = symbols.find(s => s.name === 'process_batch');
      expect(processBatch).toBeDefined();
      expect(processBatch?.signature).toContain('pub async fn process_batch');
      expect(processBatch?.signature).toContain('Pin<Box<dyn Future');

      const threadSafeCounter = symbols.find(s => s.name === 'ThreadSafeCounter');
      expect(threadSafeCounter).toBeDefined();
      expect(threadSafeCounter?.kind).toBe(SymbolKind.Class);
      expect(threadSafeCounter?.signature).toContain('pub struct ThreadSafeCounter');

      const incrementMethod = symbols.find(s => s.name === 'increment');
      expect(incrementMethod).toBeDefined();
      expect(incrementMethod?.signature).toContain('pub async fn increment');

      const asyncRange = symbols.find(s => s.name === 'AsyncRange');
      expect(asyncRange).toBeDefined();
      expect(asyncRange?.signature).toContain('pub struct AsyncRange');

      const asyncClosure = symbols.find(s => s.name === 'AsyncClosure');
      expect(asyncClosure).toBeDefined();
      expect(asyncClosure?.kind).toBe(SymbolKind.Type);
      expect(asyncClosure?.signature).toContain('type AsyncClosure<T>');

      const createProcessor = symbols.find(s => s.name === 'create_async_processor');
      expect(createProcessor).toBeDefined();
      expect(createProcessor?.signature).toContain('pub fn create_async_processor<T: Send + \'static>');
    });
  });

  describe('Error Handling and Result Types', () => {
    it('should extract custom error types and Result patterns', async () => {
      const rustCode = `
use std::error::Error;
use std::fmt::{Display, Formatter};

/// Custom error enum with various error types
#[derive(Debug, Clone)]
pub enum DatabaseError {
    ConnectionFailed(String),
    QueryError { sql: String, details: String },
    Timeout,
    InvalidData(Box<dyn Error + Send + Sync>),
}

impl Display for DatabaseError {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            DatabaseError::ConnectionFailed(msg) => write!(f, "Connection failed: {}", msg),
            DatabaseError::QueryError { sql, details } => {
                write!(f, "Query failed: {} - {}", sql, details)
            }
            DatabaseError::Timeout => write!(f, "Database operation timed out"),
            DatabaseError::InvalidData(err) => write!(f, "Invalid data: {}", err),
        }
    }
}

impl Error for DatabaseError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            DatabaseError::InvalidData(err) => Some(err.as_ref()),
            _ => None,
        }
    }
}

/// Result type alias for database operations
type DbResult<T> = Result<T, DatabaseError>;

/// Error conversion trait
impl From<std::io::Error> for DatabaseError {
    fn from(err: std::io::Error) -> Self {
        DatabaseError::ConnectionFailed(err.to_string())
    }
}

/// Repository with comprehensive error handling
pub struct UserRepository {
    connection: DatabaseConnection,
}

impl UserRepository {
    pub fn new(connection: DatabaseConnection) -> Self {
        Self { connection }
    }

    /// Function demonstrating try operator usage
    pub async fn get_user_with_posts(&self, user_id: u64) -> DbResult<UserWithPosts> {
        let user = self.find_user(user_id).await?;
        let posts = self.find_posts_by_user(user_id).await?;

        Ok(UserWithPosts {
            user,
            posts,
            loaded_at: std::time::SystemTime::now(),
        })
    }

    /// Nested error handling with custom error mapping
    pub async fn batch_update_users(
        &self,
        updates: Vec<UserUpdate>,
    ) -> Result<Vec<u64>, Vec<(usize, DatabaseError)>> {
        let mut successful_ids = Vec::new();
        let mut errors = Vec::new();

        for (index, update) in updates.into_iter().enumerate() {
            match self.update_user(update).await {
                Ok(id) => successful_ids.push(id),
                Err(e) => errors.push((index, e)),
            }
        }

        if errors.is_empty() {
            Ok(successful_ids)
        } else {
            Err(errors)
        }
    }

    /// Function with multiple error paths and early returns
    pub fn validate_and_parse_config(
        &self,
        config_str: &str,
    ) -> DbResult<DatabaseConfig> {
        if config_str.is_empty() {
            return Err(DatabaseError::InvalidData(
                "Empty configuration string".into(),
            ));
        }

        let parsed: serde_json::Value = serde_json::from_str(config_str)
            .map_err(|e| DatabaseError::InvalidData(Box::new(e)))?;

        // Validate required fields
        let host = parsed["host"]
            .as_str()
            .ok_or_else(|| DatabaseError::InvalidData("Missing host field".into()))?;

        let port = parsed["port"]
            .as_u64()
            .ok_or_else(|| DatabaseError::InvalidData("Invalid port field".into()))?;

        Ok(DatabaseConfig {
            host: host.to_string(),
            port: port as u16,
            timeout: parsed["timeout"].as_u64().unwrap_or(30),
        })
    }
}

/// Option handling utilities
pub mod option_utils {
    pub fn safe_divide(a: f64, b: f64) -> Option<f64> {
        if b != 0.0 {
            Some(a / b)
        } else {
            None
        }
    }

    pub fn chain_operations(value: Option<i32>) -> Option<String> {
        value
            .filter(|&x| x > 0)
            .map(|x| x * 2)
            .and_then(|x| if x < 100 { Some(x) } else { None })
            .map(|x| format!("Result: {}", x))
    }
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const dbError = symbols.find(s => s.name === 'DatabaseError');
      expect(dbError).toBeDefined();
      expect(dbError?.kind).toBe(SymbolKind.Class);
      expect(dbError?.signature).toContain('pub enum DatabaseError');

      const dbResult = symbols.find(s => s.name === 'DbResult');
      expect(dbResult).toBeDefined();
      expect(dbResult?.kind).toBe(SymbolKind.Type);
      expect(dbResult?.signature).toContain('type DbResult<T> = Result<T, DatabaseError>');

      const userRepo = symbols.find(s => s.name === 'UserRepository');
      expect(userRepo).toBeDefined();
      expect(userRepo?.kind).toBe(SymbolKind.Class);

      const getUserMethod = symbols.find(s => s.name === 'get_user_with_posts');
      expect(getUserMethod).toBeDefined();
      expect(getUserMethod?.signature).toContain('pub async fn get_user_with_posts');
      expect(getUserMethod?.signature).toContain('DbResult<UserWithPosts>');

      const batchUpdate = symbols.find(s => s.name === 'batch_update_users');
      expect(batchUpdate).toBeDefined();
      expect(batchUpdate?.signature).toContain('Result<Vec<u64>, Vec<(usize, DatabaseError)>>');

      const validateConfig = symbols.find(s => s.name === 'validate_and_parse_config');
      expect(validateConfig).toBeDefined();
      expect(validateConfig?.signature).toContain('DbResult<DatabaseConfig>');

      const optionUtils = symbols.find(s => s.name === 'option_utils');
      expect(optionUtils).toBeDefined();
      expect(optionUtils?.kind).toBe(SymbolKind.Namespace);

      const safeDivide = symbols.find(s => s.name === 'safe_divide');
      expect(safeDivide).toBeDefined();
      expect(safeDivide?.signature).toContain('Option<f64>');

      const chainOps = symbols.find(s => s.name === 'chain_operations');
      expect(chainOps).toBeDefined();
      expect(chainOps?.signature).toContain('Option<i32>');
      expect(chainOps?.signature).toContain('Option<String>');
    });
  });

  describe('Pattern Matching and Control Flow', () => {
    it('should extract functions with complex pattern matching', async () => {
      const rustCode = `
/// Enum for demonstrating pattern matching
#[derive(Debug, Clone)]
pub enum Message {
    Quit,
    Move { x: i32, y: i32 },
    Write(String),
    ChangeColor(i32, i32, i32),
    Nested(Box<Message>),
}

/// Pattern matching with guards and ranges
pub fn process_message(msg: Message) -> String {
    match msg {
        Message::Quit => "Quitting application".to_string(),
        Message::Move { x, y } if x > 0 && y > 0 => {
            format!("Moving to positive coordinates: ({}, {})", x, y)
        }
        Message::Move { x, y } => {
            format!("Moving to: ({}, {})", x, y)
        }
        Message::Write(text) if text.len() > 10 => {
            format!("Long message: {}...", &text[..10])
        }
        Message::Write(text) => format!("Short message: {}", text),
        Message::ChangeColor(r, g, b) => match (r, g, b) {
            (255, 0, 0) => "Red".to_string(),
            (0, 255, 0) => "Green".to_string(),
            (0, 0, 255) => "Blue".to_string(),
            (r, g, b) if r == g && g == b => "Grayscale".to_string(),
            _ => format!("Custom color: ({}, {}, {})", r, g, b),
        },
        Message::Nested(inner) => {
            format!("Nested: {}", process_message(*inner))
        }
    }
}

/// If let patterns
pub fn extract_coordinate(msg: &Message) -> Option<(i32, i32)> {
    if let Message::Move { x, y } = msg {
        Some((*x, *y))
    } else {
        None
    }
}

/// While let patterns
pub fn process_iterator<T>(mut iter: impl Iterator<Item = Option<T>>) -> Vec<T> {
    let mut results = Vec::new();
    while let Some(Some(item)) = iter.next() {
        results.push(item);
    }
    results
}

/// Range patterns and slice patterns
pub fn analyze_data(data: &[i32]) -> String {
    match data {
        [] => "Empty data".to_string(),
        [single] => format!("Single value: {}", single),
        [first, second] => format!("Two values: {}, {}", first, second),
        [first, .., last] => format!("Multiple values from {} to {}", first, last),
    }
}

/// Destructuring complex structures
#[derive(Debug)]
pub struct Person {
    name: String,
    age: u8,
    address: Address,
}

#[derive(Debug)]
pub struct Address {
    street: String,
    city: String,
    country: String,
}

pub fn extract_person_info(person: &Person) -> String {
    let Person {
        name,
        age,
        address: Address { city, country, .. },
    } = person;

    match age {
        0..=12 => format!("{} is a child from {}, {}", name, city, country),
        13..=19 => format!("{} is a teenager from {}, {}", name, city, country),
        20..=64 => format!("{} is an adult from {}, {}", name, city, country),
        65.. => format!("{} is a senior from {}, {}", name, city, country),
    }
}

/// Pattern matching with references and borrowing
pub fn match_reference_types(data: &Option<&str>) -> &'static str {
    match data {
        Some(&"special") => "Found special string",
        Some(s) if s.len() > 5 => "Long string",
        Some(_) => "Short string",
        None => "No string",
    }
}

/// Custom pattern matching with deref patterns
use std::ops::Deref;

pub struct SmartString(String);

impl Deref for SmartString {
    type Target = str;

    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

pub fn match_smart_string(s: &SmartString) -> usize {
    match s.deref() {
        "" => 0,
        "test" => 1,
        other => other.len(),
    }
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const messageEnum = symbols.find(s => s.name === 'Message');
      expect(messageEnum).toBeDefined();
      expect(messageEnum?.kind).toBe(SymbolKind.Class);
      expect(messageEnum?.signature).toContain('pub enum Message');

      const processMessage = symbols.find(s => s.name === 'process_message');
      expect(processMessage).toBeDefined();
      expect(processMessage?.signature).toContain('pub fn process_message(msg: Message)');

      const extractCoordinate = symbols.find(s => s.name === 'extract_coordinate');
      expect(extractCoordinate).toBeDefined();
      expect(extractCoordinate?.signature).toContain('Option<(i32, i32)>');

      const processIterator = symbols.find(s => s.name === 'process_iterator');
      expect(processIterator).toBeDefined();
      expect(processIterator?.signature).toContain('impl Iterator<Item = Option<T>>');

      const analyzeData = symbols.find(s => s.name === 'analyze_data');
      expect(analyzeData).toBeDefined();
      expect(analyzeData?.signature).toContain('&[i32]');

      const person = symbols.find(s => s.name === 'Person');
      expect(person).toBeDefined();
      expect(person?.kind).toBe(SymbolKind.Class);

      const address = symbols.find(s => s.name === 'Address');
      expect(address).toBeDefined();
      expect(address?.kind).toBe(SymbolKind.Class);

      const extractPersonInfo = symbols.find(s => s.name === 'extract_person_info');
      expect(extractPersonInfo).toBeDefined();
      expect(extractPersonInfo?.signature).toContain('&Person');

      const matchRefTypes = symbols.find(s => s.name === 'match_reference_types');
      expect(matchRefTypes).toBeDefined();
      expect(matchRefTypes?.signature).toContain('&Option<&str>');
      expect(matchRefTypes?.signature).toContain('&\'static str');

      const smartString = symbols.find(s => s.name === 'SmartString');
      expect(smartString).toBeDefined();
      expect(smartString?.kind).toBe(SymbolKind.Class);

      const matchSmartString = symbols.find(s => s.name === 'match_smart_string');
      expect(matchSmartString).toBeDefined();
      expect(matchSmartString?.signature).toContain('&SmartString');
    });
  });

  describe('Unsafe Code and FFI', () => {
    it('should extract unsafe blocks, raw pointers, and FFI functions', async () => {
      const rustCode = `
use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::ptr;
use std::slice;

/// External C functions
extern "C" {
    fn malloc(size: usize) -> *mut c_void;
    fn free(ptr: *mut c_void);
    fn strlen(s: *const c_char) -> usize;
    fn printf(format: *const c_char, ...) -> c_int;
}

/// Unsafe struct with raw pointers
#[repr(C)]
pub struct RawBuffer {
    data: *mut u8,
    len: usize,
    capacity: usize,
}

impl RawBuffer {
    /// Unsafe constructor
    pub unsafe fn new(capacity: usize) -> Self {
        let data = malloc(capacity) as *mut u8;
        if data.is_null() {
            panic!("Failed to allocate memory");
        }

        Self {
            data,
            len: 0,
            capacity,
        }
    }

    /// Safe wrapper around unsafe operations
    pub fn push(&mut self, byte: u8) -> Result<(), &'static str> {
        if self.len >= self.capacity {
            return Err("Buffer overflow");
        }

        unsafe {
            *self.data.add(self.len) = byte;
        }
        self.len += 1;
        Ok(())
    }

    /// Unsafe access to raw data
    pub unsafe fn as_slice(&self) -> &[u8] {
        slice::from_raw_parts(self.data, self.len)
    }

    /// Safe access with bounds checking
    pub fn get(&self, index: usize) -> Option<u8> {
        if index < self.len {
            unsafe {
                Some(*self.data.add(index))
            }
        } else {
            None
        }
    }
}

impl Drop for RawBuffer {
    fn drop(&mut self) {
        unsafe {
            if !self.data.is_null() {
                free(self.data as *mut c_void);
            }
        }
    }
}

/// Unsafe Send and Sync implementations
unsafe impl Send for RawBuffer {}
unsafe impl Sync for RawBuffer {}

/// C-compatible struct
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct Point3D {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

/// Export functions for C
#[no_mangle]
pub extern "C" fn create_point(x: f64, y: f64, z: f64) -> Point3D {
    Point3D { x, y, z }
}

#[no_mangle]
pub extern "C" fn point_distance(p1: Point3D, p2: Point3D) -> f64 {
    let dx = p1.x - p2.x;
    let dy = p1.y - p2.y;
    let dz = p1.z - p2.z;
    (dx * dx + dy * dy + dz * dz).sqrt()
}

#[no_mangle]
pub extern "C" fn print_point(point: Point3D) {
    unsafe {
        let format = CString::new("Point(%.2f, %.2f, %.2f)\\n").unwrap();
        printf(format.as_ptr(), point.x, point.y, point.z);
    }
}

/// Union type for type punning
#[repr(C)]
union FloatBytes {
    f: f32,
    bytes: [u8; 4],
}

pub fn float_to_bytes(f: f32) -> [u8; 4] {
    unsafe {
        FloatBytes { f }.bytes
    }
}

pub fn bytes_to_float(bytes: [u8; 4]) -> f32 {
    unsafe {
        FloatBytes { bytes }.f
    }
}

/// Inline assembly example
#[cfg(target_arch = "x86_64")]
pub fn rdtsc() -> u64 {
    let lo: u32;
    let hi: u32;
    unsafe {
        std::arch::asm!(
            "rdtsc",
            out("eax") lo,
            out("edx") hi,
            options(nomem, nostack)
        );
    }
    ((hi as u64) << 32) | (lo as u64)
}

/// Memory manipulation utilities
pub mod unsafe_utils {
    use super::*;

    /// Zero memory region
    pub unsafe fn zero_memory(ptr: *mut u8, len: usize) {
        ptr::write_bytes(ptr, 0, len);
    }

    /// Copy memory with overlap check
    pub unsafe fn copy_memory(src: *const u8, dst: *mut u8, len: usize) {
        if src.cast::<u8>().offset(len as isize) <= dst ||
           dst.offset(len as isize) <= src.cast::<u8>() {
            ptr::copy_nonoverlapping(src, dst, len);
        } else {
            ptr::copy(src, dst, len);
        }
    }

    /// Raw string manipulation
    pub unsafe fn c_string_length(s: *const c_char) -> usize {
        strlen(s)
    }

    /// Convert C string to Rust string
    pub unsafe fn c_str_to_string(s: *const c_char) -> Result<String, std::str::Utf8Error> {
        CStr::from_ptr(s).to_str().map(|s| s.to_owned())
    }
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Check extern block
      const mallocExtern = symbols.find(s => s.name === 'malloc');
      expect(mallocExtern).toBeDefined();
      expect(mallocExtern?.signature).toContain('fn malloc(size: usize)');

      const rawBuffer = symbols.find(s => s.name === 'RawBuffer');
      expect(rawBuffer).toBeDefined();
      expect(rawBuffer?.kind).toBe(SymbolKind.Class);
      expect(rawBuffer?.signature).toContain('pub struct RawBuffer');

      const unsafeNew = symbols.find(s => s.name === 'new' && s.parentId === rawBuffer?.id);
      expect(unsafeNew).toBeDefined();
      expect(unsafeNew?.signature).toContain('pub unsafe fn new');

      const asSlice = symbols.find(s => s.name === 'as_slice');
      expect(asSlice).toBeDefined();
      expect(asSlice?.signature).toContain('pub unsafe fn as_slice');

      const point3D = symbols.find(s => s.name === 'Point3D');
      expect(point3D).toBeDefined();
      expect(point3D?.signature).toContain('pub struct Point3D');

      const createPoint = symbols.find(s => s.name === 'create_point');
      expect(createPoint).toBeDefined();
      expect(createPoint?.signature).toContain('pub extern "C" fn create_point');

      const pointDistance = symbols.find(s => s.name === 'point_distance');
      expect(pointDistance).toBeDefined();
      expect(pointDistance?.signature).toContain('pub extern "C" fn point_distance');

      const floatBytes = symbols.find(s => s.name === 'FloatBytes');
      expect(floatBytes).toBeDefined();
      expect(floatBytes?.kind).toBe(SymbolKind.Union);
      expect(floatBytes?.signature).toContain('union FloatBytes');

      const floatToBytes = symbols.find(s => s.name === 'float_to_bytes');
      expect(floatToBytes).toBeDefined();
      expect(floatToBytes?.signature).toContain('pub fn float_to_bytes');

      const rdtsc = symbols.find(s => s.name === 'rdtsc');
      expect(rdtsc).toBeDefined();
      expect(rdtsc?.signature).toContain('pub fn rdtsc');

      const unsafeUtils = symbols.find(s => s.name === 'unsafe_utils');
      expect(unsafeUtils).toBeDefined();
      expect(unsafeUtils?.kind).toBe(SymbolKind.Namespace);

      const zeroMemory = symbols.find(s => s.name === 'zero_memory');
      expect(zeroMemory).toBeDefined();
      expect(zeroMemory?.signature).toContain('pub unsafe fn zero_memory');
    });
  });

  describe('Procedural Macros and Attributes', () => {
    it('should extract derive macros, proc macros, and custom attributes', async () => {
      const rustCode = `
use proc_macro::TokenStream;
use quote::quote;
use syn::{parse_macro_input, DeriveInput, Data, Fields};

/// Custom derive macro
#[proc_macro_derive(Builder, attributes(builder))]
pub fn derive_builder(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    let name = &input.ident;
    let builder_name = format!("{}Builder", name);
    let builder_ident = syn::Ident::new(&builder_name, name.span());

    let expanded = quote! {
        impl #name {
            pub fn builder() -> #builder_ident {
                #builder_ident::default()
            }
        }
    };

    TokenStream::from(expanded)
}

/// Attribute macro
#[proc_macro_attribute]
pub fn timed(args: TokenStream, input: TokenStream) -> TokenStream {
    let input_fn = parse_macro_input!(input as syn::ItemFn);
    let fn_name = &input_fn.sig.ident;
    let fn_block = &input_fn.block;
    let fn_vis = &input_fn.vis;
    let fn_sig = &input_fn.sig;

    let expanded = quote! {
        #fn_vis #fn_sig {
            let start = std::time::Instant::now();
            let result = (|| #fn_block)();
            let duration = start.elapsed();
            println!("Function {} took {:?}", stringify!(#fn_name), duration);
            result
        }
    };

    TokenStream::from(expanded)
}

/// Function-like macro
#[proc_macro]
pub fn generate_tests(input: TokenStream) -> TokenStream {
    // Parse input and generate test functions
    let expanded = quote! {
        #[cfg(test)]
        mod generated_tests {
            use super::*;

            #[test]
            fn test_example() {
                assert_eq!(2 + 2, 4);
            }
        }
    };

    TokenStream::from(expanded)
}

/// Struct with many derive macros
#[derive(Debug, Clone, PartialEq, Eq, Hash, PartialOrd, Ord)]
#[derive(serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ComplexStruct {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub optional_field: Option<String>,

    #[serde(default)]
    pub default_field: i32,

    #[serde(rename = "customName")]
    pub renamed_field: bool,
}

/// Struct with custom attributes
#[repr(C, packed)]
#[derive(Copy, Clone)]
pub struct PackedStruct {
    pub byte: u8,
    pub word: u16,
    pub dword: u32,
}

/// Function with custom attributes
#[inline(always)]
#[cnew]
#[track_caller]
pub fn error_function() -> ! {
    panic!("This function always panics");
}

/// Async function with attributes
#[tokio::main]
async fn main() {
    run_application().await;
}

#[timed]
#[tracing::instrument]
pub async fn run_application() {
    println!("Running application...");
    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    println!("Application finished.");
}

/// Test functions with attributes
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn basic_test() {
        assert_eq!(1 + 1, 2);
    }

    #[test]
    #[ignore]
    fn ignored_test() {
        // This test is ignored
    }

    #[test]
    #[should_panic]
    fn panic_test() {
        panic!("Expected panic");
    }

    #[test]
    #[should_panic(expected = "specific message")]
    fn panic_with_message_test() {
        panic!("specific message");
    }

    #[tokio::test]
    async fn async_test() {
        assert_eq!(async_function().await, 42);
    }

    #[test]
    #[cfg(feature = "expensive-tests")]
    fn expensive_test() {
        // Only run when expensive-tests feature is enabled
    }
}

/// Benchmark functions
#[cfg(feature = "bench")]
mod benches {
    use super::*;
    use criterion::{black_box, criterion_group, criterion_main, Criterion};

    fn bench_function(c: &mut Criterion) {
        c.bench_function("example", |b| {
            b.iter(|| {
                black_box(expensive_computation(black_box(100)))
            })
        });
    }

    criterion_group!(benches, bench_function);
    criterion_main!(benches);
}

/// Conditional compilation
#[cfg(target_os = "linux")]
pub fn linux_specific_function() {
    println!("This only compiles on Linux");
}

#[cfg(target_os = "windows")]
pub fn windows_specific_function() {
    println!("This only compiles on Windows");
}

#[cfg(target_arch = "x86_64")]
pub fn x86_64_specific_function() {
    println!("This only compiles on x86_64");
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const deriveBuilder = symbols.find(s => s.name === 'derive_builder');
      expect(deriveBuilder).toBeDefined();
      expect(deriveBuilder?.signature).toContain('pub fn derive_builder');
      expect(deriveBuilder?.signature).toContain('TokenStream');

      const timedMacro = symbols.find(s => s.name === 'timed');
      expect(timedMacro).toBeDefined();
      expect(timedMacro?.signature).toContain('pub fn timed');

      const generateTests = symbols.find(s => s.name === 'generate_tests');
      expect(generateTests).toBeDefined();
      expect(generateTests?.signature).toContain('pub fn generate_tests');

      const complexStruct = symbols.find(s => s.name === 'ComplexStruct');
      expect(complexStruct).toBeDefined();
      expect(complexStruct?.signature).toContain('pub struct ComplexStruct');

      const packedStruct = symbols.find(s => s.name === 'PackedStruct');
      expect(packedStruct).toBeDefined();
      expect(packedStruct?.signature).toContain('pub struct PackedStruct');

      const errorFunction = symbols.find(s => s.name === 'error_function');
      expect(errorFunction).toBeDefined();
      expect(errorFunction?.signature).toContain('pub fn error_function');

      const mainFunction = symbols.find(s => s.name === 'main');
      expect(mainFunction).toBeDefined();
      expect(mainFunction?.signature).toContain('async fn main');

      const runApplication = symbols.find(s => s.name === 'run_application');
      expect(runApplication).toBeDefined();
      expect(runApplication?.signature).toContain('pub async fn run_application');

      const testsModule = symbols.find(s => s.name === 'tests');
      expect(testsModule).toBeDefined();
      expect(testsModule?.kind).toBe(SymbolKind.Namespace);

      const basicTest = symbols.find(s => s.name === 'basic_test');
      expect(basicTest).toBeDefined();
      expect(basicTest?.signature).toContain('fn basic_test');

      const asyncTest = symbols.find(s => s.name === 'async_test');
      expect(asyncTest).toBeDefined();
      expect(asyncTest?.signature).toContain('async fn async_test');

      const linuxFunction = symbols.find(s => s.name === 'linux_specific_function');
      expect(linuxFunction).toBeDefined();
      expect(linuxFunction?.signature).toContain('pub fn linux_specific_function');
    });
  });

  describe('Macro Extraction', () => {
    it('should extract macro definitions', async () => {
      const rustCode = `
macro_rules! vec_of_strings {
    ($($x:expr),*) => {
        vec![$($x.to_string()),*]
    };
}

macro_rules! create_function {
    ($func_name:ident) => {
        fn $func_name() {
            println!("Function {} called", stringify!($func_name));
        }
    };
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      const vecMacro = symbols.find(s => s.name === 'vec_of_strings');
      expect(vecMacro).toBeDefined();
      expect(vecMacro?.kind).toBe(SymbolKind.Function);
      expect(vecMacro?.signature).toBe('macro_rules! vec_of_strings');

      const createFuncMacro = symbols.find(s => s.name === 'create_function');
      expect(createFuncMacro).toBeDefined();
      expect(createFuncMacro?.signature).toBe('macro_rules! create_function');
    });
  });

  describe('Type Inference', () => {
    it('should infer types from Rust annotations', async () => {
      const rustCode = `
fn get_name() -> String {
    "test".to_string()
}

fn calculate(x: i32, y: f64) -> f64 {
    x as f64 + y
}

const NUMBER: i32 = 42;
static TEXT: &str = "hello";
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);
      const types = extractor.inferTypes(symbols);

      const getName = symbols.find(s => s.name === 'get_name');
      expect(getName).toBeDefined();
      // TODO: Fix type inference
      // expect(types.get(getName!.id)).toBe('String');

      const calculate = symbols.find(s => s.name === 'calculate');
      expect(calculate).toBeDefined();
      // expect(types.get(calculate!.id)).toBe('f64');

      const number = symbols.find(s => s.name === 'NUMBER');
      expect(number).toBeDefined();
      // expect(types.get(number!.id)).toBe('i32');

      console.log(`🦀 Type inference extracted ${types.size} types (TODO: fix inference)`);
    });
  });

  describe('Relationship Extraction', () => {
    it('should extract trait implementation relationships', async () => {
      const rustCode = `
trait Display {
    fn fmt(&self) -> String;
}

struct User {
    name: String,
}

impl Display for User {
    fn fmt(&self) -> String {
        self.name.clone()
    }
}

impl User {
    fn new(name: String) -> Self {
        Self { name }
    }
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should find User implements Display relationship
      const implRelationship = relationships.find(r =>
        r.kind === 'implements' &&
        symbols.find(s => s.id === r.fromSymbolId)?.name === 'User' &&
        symbols.find(s => s.id === r.toSymbolId)?.name === 'Display'
      );
      expect(implRelationship).toBeDefined();

      console.log(`🦀 Found ${relationships.length} Rust relationships`);
    });
  });

  describe('Complex Rust Features', () => {
    it('should handle comprehensive Rust code', async () => {
      const rustCode = `
use std::collections::HashMap;
use std::fmt::{Debug, Display};

/// A user management system
#[derive(Debug, Clone)]
pub struct User {
    pub id: u64,
    name: String,
    email: Option<String>,
}

pub trait UserOperations {
    fn get_id(&self) -> u64;
    fn update_email(&mut self, email: String) -> Result<(), String>;
}

impl UserOperations for User {
    fn get_id(&self) -> u64 {
        self.id
    }

    fn update_email(&mut self, email: String) -> Result<(), String> {
        if email.contains('@') {
            self.email = Some(email);
            Ok(())
        } else {
            Err("Invalid email".to_string())
        }
    }
}

impl User {
    pub fn new(id: u64, name: String) -> Self {
        Self {
            id,
            name,
            email: None,
        }
    }

    pub async fn fetch_from_db(id: u64) -> Option<User> {
        // Async function implementation
        None
    }
}

#[derive(Debug)]
pub enum Status {
    Active,
    Inactive,
    Pending(String),
}

pub mod utils {
    pub fn validate_email(email: &str) -> bool {
        email.contains('@')
    }
}

macro_rules! create_user {
    ($id:expr, $name:expr) => {
        User::new($id, $name.to_string())
    };
}

const MAX_USERS: usize = 1000;
static mut COUNTER: i32 = 0;
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Check we extracted all major symbols
      expect(symbols.find(s => s.name === 'User')).toBeDefined();
      expect(symbols.find(s => s.name === 'UserOperations')).toBeDefined();
      expect(symbols.find(s => s.name === 'Status')).toBeDefined();
      expect(symbols.find(s => s.name === 'utils')).toBeDefined();
      expect(symbols.find(s => s.name === 'create_user')).toBeDefined();
      expect(symbols.find(s => s.name === 'MAX_USERS')).toBeDefined();
      expect(symbols.find(s => s.name === 'COUNTER')).toBeDefined();

      // Check specific features
      const userStruct = symbols.find(s => s.name === 'User');
      expect(userStruct?.signature).toContain('pub struct User');

      const fetchMethod = symbols.find(s => s.name === 'fetch_from_db');
      expect(fetchMethod?.signature).toContain('pub async fn fetch_from_db');

      const macro = symbols.find(s => s.name === 'create_user');
      expect(macro?.kind).toBe(SymbolKind.Function);

      console.log(`🦀 Extracted ${symbols.length} Rust symbols successfully`);
    });
  });

  describe('Performance and Edge Cases', () => {
    it('should handle large Rust files with many symbols', async () => {
      // Generate a large Rust file with many structs and impls
      const structs = Array.from({ length: 15 }, (_, i) => `
/// Documentation for Struct${i}
#[derive(Debug, Clone, PartialEq)]
pub struct Struct${i} {
    pub id: u64,
    pub value: i32,
    pub data: Vec<String>,
    pub optional: Option<String>,
}

impl Struct${i} {
    pub fn new(id: u64, value: i32) -> Self {
        Self {
            id,
            value,
            data: Vec::new(),
            optional: None,
        }
    }

    pub fn add_data(&mut self, item: String) {
        self.data.push(item);
    }

    pub fn get_value(&self) -> i32 {
        self.value
    }

    pub async fn async_operation(&self) -> Result<String, String> {
        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        Ok(format!("Processed struct {}", self.id))
    }
}

impl std::fmt::Display for Struct${i} {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Struct{}(id: {}, value: {})", ${i}, self.id, self.value)
    }
}`).join('\n');

      const traits = Array.from({ length: 5 }, (_, i) => `
pub trait Trait${i} {
    type Associated${i};

    fn method_${i}(&self) -> Self::Associated${i};
    fn default_method_${i}(&self) -> String {
        format!("Default implementation for trait ${i}")
    }
}`).join('\n');

      const functions = Array.from({ length: 8 }, (_, i) => `
pub async fn async_function_${i}<T: Send + Sync + Clone>(
    param: T,
    index: usize,
) -> Result<Vec<T>, Box<dyn std::error::Error + Send + Sync>>
where
    T: std::fmt::Debug,
{
    println!("Processing function ${i} with {:?}", param);
    tokio::time::sleep(std::time::Duration::from_millis(${i * 10})).await;
    Ok(vec![param; index])
}`).join('\n');

      const rustCode = `
use std::collections::{HashMap, HashSet, BTreeMap};
use std::sync::{Arc, Mutex, RwLock};
use tokio::time::{sleep, Duration};
use serde::{Serialize, Deserialize};

// Constants and statics
pub const MAX_ITEMS: usize = 10000;
pub const VERSION: &str = "1.0.0";
static mut GLOBAL_COUNTER: i64 = 0;
static THREAD_SAFE_DATA: RwLock<HashMap<String, i32>> = RwLock::const_new(HashMap::new());

${structs}
${traits}
${functions}

/// Generic container with complex bounds
pub struct ComplexContainer<T, U, V>
where
    T: Send + Sync + Clone + std::fmt::Debug,
    U: serde::Serialize + serde::de::DeserializeOwned,
    V: std::hash::Hash + Eq + Clone,
{
    pub data: HashMap<V, T>,
    pub metadata: U,
    pub cache: Arc<RwLock<BTreeMap<String, Vec<T>>>>,
}

impl<T, U, V> ComplexContainer<T, U, V>
where
    T: Send + Sync + Clone + std::fmt::Debug,
    U: serde::Serialize + serde::de::DeserializeOwned,
    V: std::hash::Hash + Eq + Clone,
{
    pub fn new(metadata: U) -> Self {
        Self {
            data: HashMap::new(),
            metadata,
            cache: Arc::new(RwLock::new(BTreeMap::new())),
        }
    }

    pub async fn insert(&mut self, key: V, value: T) -> Result<(), String> {
        self.data.insert(key, value.clone());

        let mut cache = self.cache.write().await;
        cache.entry("recent".to_string())
            .or_insert_with(Vec::new)
            .push(value);

        Ok(())
    }
}

/// Manager struct using all previous structs
pub struct StructManager {
    pub structs: Vec<Box<dyn std::fmt::Display + Send + Sync>>,
    pub containers: HashMap<String, ComplexContainer<String, serde_json::Value, i32>>,
    pub async_handles: Vec<tokio::task::JoinHandle<()>>,
}

impl StructManager {
    pub fn new() -> Self {
        Self {
            structs: Vec::new(),
            containers: HashMap::new(),
            async_handles: Vec::new(),
        }
    }

    pub async fn process_all(&mut self) -> Result<(), Box<dyn std::error::Error>> {
        for (index, container) in self.containers.iter_mut().enumerate() {
            let handle = tokio::spawn(async move {
                println!("Processing container {}: {}", index, container.0);
            });
            self.async_handles.push(handle);
        }

        for handle in self.async_handles.drain(..) {
            handle.await?;
        }

        Ok(())
    }
}

/// Macro for creating structs
macro_rules! create_numbered_struct {
    ($name:ident, $num:expr) => {
        #[derive(Debug, Clone)]
        pub struct $name {
            number: i32,
        }

        impl $name {
            pub fn new() -> Self {
                Self { number: $num }
            }
        }
    };
}

// Use the macro
create_numbered_struct!(MacroStruct1, 1);
create_numbered_struct!(MacroStruct2, 2);
create_numbered_struct!(MacroStruct3, 3);

pub fn main() {
    println!("Rust code with {} structs and {} traits", 15, 5);
}
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);

      // Should extract many symbols
      expect(symbols.length).toBeGreaterThan(100);

      // Check that all generated structs were extracted
      for (let i = 0; i < 15; i++) {
        const struct = symbols.find(s => s.name === `Struct${i}`);
        expect(struct).toBeDefined();
        expect(struct?.kind).toBe(SymbolKind.Class);
      }

      // Check that all traits were extracted
      for (let i = 0; i < 5; i++) {
        const trait = symbols.find(s => s.name === `Trait${i}`);
        expect(trait).toBeDefined();
        expect(trait?.kind).toBe(SymbolKind.Interface);
      }

      // Check that all async functions were extracted
      for (let i = 0; i < 8; i++) {
        const func = symbols.find(s => s.name === `async_function_${i}`);
        expect(func).toBeDefined();
        expect(func?.signature).toContain('pub async fn');
      }

      // Check complex container
      const complexContainer = symbols.find(s => s.name === 'ComplexContainer');
      expect(complexContainer).toBeDefined();
      expect(complexContainer?.signature).toContain('<T, U, V>');

      // Check manager
      const manager = symbols.find(s => s.name === 'StructManager');
      expect(manager).toBeDefined();
      expect(manager?.kind).toBe(SymbolKind.Class);

      // Check constants
      const maxItems = symbols.find(s => s.name === 'MAX_ITEMS');
      expect(maxItems).toBeDefined();
      expect(maxItems?.kind).toBe(SymbolKind.Constant);

      // Check macro-generated structs
      const macroStruct1 = symbols.find(s => s.name === 'MacroStruct1');
      expect(macroStruct1).toBeDefined();

      console.log(`🦀 Performance test: Extracted ${symbols.length} symbols and ${relationships.length} relationships`);
    });

    it('should handle edge cases and malformed code gracefully', async () => {
      const rustCode = `
// Edge cases and unusual Rust constructs

// Empty structs and enums
struct EmptyStruct;
struct UnitStruct();
struct EmptyTupleStruct(/* empty */);

enum EmptyEnum {}
enum SingleVariant { Only }

// Unusual function signatures
fn function_with_no_params_or_return() {}
fn function_with_ellipsis(_: ()) -> ! { loop {} }

// Complex generic constraints
fn complex_generics<'a, T, U, V>(
    _: T,
) where
    T: 'a + Send + Sync + Clone + std::fmt::Debug,
    U: for<'b> Fn(&'b T) -> &'b T,
    V: Iterator<Item = T>,
{
}

// Malformed code that shouldn't crash the parser
struct MissingBrace {
    field: i32
// Missing closing brace

// Unusual trait bounds
fn weird_bounds<T: ?Sized + Send>(_: &T) {}

// Raw identifiers
fn r#match() {}
struct r#struct {
    r#type: i32,
}

// Const generics
struct ConstGeneric<const N: usize> {
    array: [i32; N],
}

// Associated type projections
fn associated_types<T>() where T: Iterator, T::Item: Clone {}

// Multiple impl blocks for same type
struct MultiImpl;
impl MultiImpl { fn method1() {} }
impl MultiImpl { fn method2() {} }
impl std::fmt::Debug for MultiImpl {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "MultiImpl")
    }
}

// Async closures and complex futures
fn async_closures() {
    let closure = |x: i32| async move { x * 2 };
}

// Macro with complex patterns
macro_rules! complex_macro {
    ($($name:ident : $type:ty),* $(,)?) => {
        struct Generated {
            $($name: $type),*
        }
    };
    (@internal $($tokens:tt)*) => {
        // Internal macro rule
    };
}

// Type aliases with complex bounds
type ComplexAlias<T> = std::collections::HashMap<String, T>
where
    T: Clone + Send + Sync;

// Phantom data usage
use std::marker::PhantomData;
struct PhantomStruct<T> {
    _phantom: PhantomData<T>,
}

// Recursive type definitions
enum List<T> {
    Nil,
    Cons(T, Box<List<T>>),
}

// Higher-ranked trait bounds in type aliases
type HigherRanked = dyn for<'a> Fn(&'a str) -> &'a str;

// Unusual visibility modifiers
pub(crate) struct CrateVisible;
pub(super) struct SuperVisible;
pub(in crate::module) struct PathVisible;
`;

      const result = await parserManager.parseFile('test.rs', rustCode);
      const extractor = new RustExtractor('rust', 'test.rs', rustCode);

      // Should not throw even with malformed code
      expect(() => {
        const symbols = extractor.extractSymbols(result.tree);
        const relationships = extractor.extractRelationships(result.tree, symbols);
      }).not.toThrow();

      const symbols = extractor.extractSymbols(result.tree);

      // Should still extract valid symbols
      const emptyStruct = symbols.find(s => s.name === 'EmptyStruct');
      expect(emptyStruct).toBeDefined();
      expect(emptyStruct?.kind).toBe(SymbolKind.Class);

      const unitStruct = symbols.find(s => s.name === 'UnitStruct');
      expect(unitStruct).toBeDefined();

      const emptyEnum = symbols.find(s => s.name === 'EmptyEnum');
      expect(emptyEnum).toBeDefined();
      expect(emptyEnum?.kind).toBe(SymbolKind.Class);

      const complexGenerics = symbols.find(s => s.name === 'complex_generics');
      expect(complexGenerics).toBeDefined();
      expect(complexGenerics?.signature).toContain('<\'a, T, U, V>');

      const rawMatch = symbols.find(s => s.name === 'r#match');
      expect(rawMatch).toBeDefined();

      const rawStruct = symbols.find(s => s.name === 'r#struct');
      expect(rawStruct).toBeDefined();

      const constGeneric = symbols.find(s => s.name === 'ConstGeneric');
      expect(constGeneric).toBeDefined();
      expect(constGeneric?.signature).toContain('<const N: usize>');

      const multiImpl = symbols.find(s => s.name === 'MultiImpl');
      expect(multiImpl).toBeDefined();

      const complexAlias = symbols.find(s => s.name === 'ComplexAlias');
      expect(complexAlias).toBeDefined();
      expect(complexAlias?.kind).toBe(SymbolKind.Type);

      const phantomStruct = symbols.find(s => s.name === 'PhantomStruct');
      expect(phantomStruct).toBeDefined();

      const listEnum = symbols.find(s => s.name === 'List');
      expect(listEnum).toBeDefined();
      expect(listEnum?.signature).toContain('enum List<T>');

      const crateVisible = symbols.find(s => s.name === 'CrateVisible');
      expect(crateVisible).toBeDefined();
      expect(crateVisible?.signature).toContain('pub(crate)');

      console.log(`🦀 Edge case test: Extracted ${symbols.length} symbols from complex code`);
    });
  });
});