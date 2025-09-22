/**
 * MillerEmbedder Tests - Validate embedding generation with code samples
 */

import { describe, test, expect, beforeAll, afterAll } from 'bun:test';
import MillerEmbedder, { type CodeContext, type EmbeddingResult } from '../../embeddings/miller-embedder.js';

describe('MillerEmbedder', () => {
  let embedder: MillerEmbedder;

  beforeAll(async () => {
    embedder = new MillerEmbedder();
    // Initialize with fast model for testing
    await embedder.initialize('fast');
  });

  afterAll(() => {
    embedder.clearCache();
  });

  test('should initialize with MiniLM model successfully', async () => {
    const modelInfo = embedder.getModelInfo();
    expect(modelInfo).toBeTruthy();
    expect(modelInfo?.name).toBe('Xenova/all-MiniLM-L6-v2');
    expect(modelInfo?.dimensions).toBe(384);
    expect(modelInfo?.speed).toBe('fast');
  });

  test('should generate embeddings for simple TypeScript function', async () => {
    const code = `
function calculateTotal(price: number, tax: number): number {
  return price + (price * tax);
}`;

    const context: CodeContext = {
      file: 'calculator.ts',
      language: 'typescript',
      layer: 'domain'
    };

    const result: EmbeddingResult = await embedder.embedCode(code, context);

    expect(result.vector).toBeInstanceOf(Float32Array);
    expect(result.dimensions).toBe(384);
    expect(result.model).toBe('Xenova/all-MiniLM-L6-v2');
    expect(result.confidence).toBeGreaterThan(0);
    expect(result.timestamp).toBeGreaterThan(0);
  });

  test('should generate embeddings for TypeScript interface (DTO pattern)', async () => {
    const code = `
interface IUserDto {
  id: string;
  email: string;
  name: string;
  createdAt: Date;
}`;

    const context: CodeContext = {
      file: 'types/user.ts',
      language: 'typescript',
      layer: 'frontend',
      exports: ['IUserDto'],
      patterns: ['dto', 'interface']
    };

    const result = await embedder.embedCode(code, context);

    expect(result.vector).toBeInstanceOf(Float32Array);
    expect(result.dimensions).toBe(384);
    expect(result.confidence).toBeGreaterThan(0.6); // Should be high confidence with context
  });

  test('should generate embeddings for C# class (Entity pattern)', async () => {
    const code = `
public class User : Entity
{
    public string Id { get; private set; }
    public string Email { get; private set; }
    public string Name { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public User(string email, string name)
    {
        Id = Guid.NewGuid().ToString();
        Email = email;
        Name = name;
        CreatedAt = DateTime.UtcNow;
    }
}`;

    const context: CodeContext = {
      file: 'Models/User.cs',
      language: 'csharp',
      layer: 'domain',
      patterns: ['entity', 'domain-model']
    };

    const result = await embedder.embedCode(code, context);

    expect(result.vector).toBeInstanceOf(Float32Array);
    expect(result.dimensions).toBe(384);
    expect(result.confidence).toBeGreaterThan(0.7); // High confidence with rich context
  });

  test('should handle batch embedding generation', async () => {
    const codeSnippets = [
      {
        code: `function getUserById(id: string): Promise<User> { return userRepo.findById(id); }`,
        context: { file: 'user-service.ts', language: 'typescript', layer: 'api' as const }
      },
      {
        code: `public async Task<User> GetUserByIdAsync(string id) { return await _userRepository.GetByIdAsync(id); }`,
        context: { file: 'UserService.cs', language: 'csharp', layer: 'api' as const }
      },
      {
        code: `SELECT id, email, name FROM users WHERE id = ?`,
        context: { file: 'queries.sql', language: 'sql', layer: 'database' as const }
      }
    ];

    const results = await embedder.embedBatch(codeSnippets);

    expect(results).toHaveLength(3);
    results.forEach(result => {
      expect(result.vector).toBeInstanceOf(Float32Array);
      expect(result.dimensions).toBe(384);
      expect(result.confidence).toBeGreaterThan(0);
    });
  });

  test('should generate similar embeddings for semantically related code', async () => {
    // Test the cross-layer entity mapping concept
    const tsInterface = `
interface IUserDto {
  id: string;
  email: string;
  name: string;
}`;

    const csClass = `
public class UserDto {
  public string Id { get; set; }
  public string Email { get; set; }
  public string Name { get; set; }
}`;

    const tsResult = await embedder.embedCode(tsInterface, {
      file: 'types/user.ts',
      language: 'typescript',
      patterns: ['dto']
    });

    const csResult = await embedder.embedCode(csClass, {
      file: 'DTOs/UserDto.cs',
      language: 'csharp',
      patterns: ['dto']
    });

    // Calculate cosine similarity between the vectors
    const similarity = calculateCosineSimilarity(tsResult.vector, csResult.vector);

    // Should be reasonably similar (>0.7) since they represent the same entity concept
    expect(similarity).toBeGreaterThan(0.7);
  });

  test('should use caching for repeated embeddings', async () => {
    const code = `function hello() { console.log('Hello World!'); }`;

    const start1 = Date.now();
    const result1 = await embedder.embedCode(code);
    const time1 = Date.now() - start1;

    const start2 = Date.now();
    const result2 = await embedder.embedCode(code);
    const time2 = Date.now() - start2;

    // Second call should be much faster (cached)
    expect(time2).toBeLessThan(time1);
    expect(result2.vector).toEqual(result1.vector);

    const cacheStats = embedder.getCacheStats();
    expect(cacheStats.size).toBeGreaterThan(0);
  });

  test('should handle large code with chunking', async () => {
    // Create a large code sample that exceeds token limits
    const largeCode = `
class LargeService {
  ${Array.from({ length: 50 }, (_, i) => `
  public method${i}(param: string): string {
    // This is method ${i} with some complex logic
    const result = param.split('').reverse().join('');
    return \`processed_\${result}_method${i}\`;
  }`).join('\n')}
}`;

    const result = await embedder.embedCode(largeCode, {
      file: 'large-service.ts',
      language: 'typescript'
    });

    expect(result.vector).toBeInstanceOf(Float32Array);
    expect(result.dimensions).toBe(384);
    expect(result.confidence).toBeGreaterThan(0);
  });

  test('should generate query embeddings for semantic search', async () => {
    const queryResult = await embedder.embedQuery(
      'user authentication login',
      { language: 'typescript', pattern: 'service' }
    );

    expect(queryResult.vector).toBeInstanceOf(Float32Array);
    expect(queryResult.dimensions).toBe(384);
  });
});

/**
 * Calculate cosine similarity between two vectors
 */
function calculateCosineSimilarity(a: Float32Array, b: Float32Array): number {
  if (a.length !== b.length) {
    throw new Error('Vectors must have same length');
  }

  let dotProduct = 0;
  let normA = 0;
  let normB = 0;

  for (let i = 0; i < a.length; i++) {
    dotProduct += a[i] * b[i];
    normA += a[i] * a[i];
    normB += b[i] * b[i];
  }

  return dotProduct / (Math.sqrt(normA) * Math.sqrt(normB));
}