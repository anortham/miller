import { MillerEmbedder } from './src/embeddings/miller-embedder.js';

async function benchmarkEmbeddingModels() {
  console.log('🧪 Benchmarking Embedding Models for Code Intelligence\n');

  // Test code samples - different types to see specialization benefits
  const codeSamples = [
    {
      name: 'Simple Function',
      code: `
function calculateTotal(items: Item[]): number {
  return items.reduce((sum, item) => sum + item.price, 0);
}
      `.trim()
    },
    {
      name: 'Class with Methods',
      code: `
class UserRepository {
  constructor(private db: Database) {}

  async findById(id: string): Promise<User | null> {
    return this.db.users.findOne({ id });
  }

  async save(user: User): Promise<void> {
    await this.db.users.save(user);
  }
}
      `.trim()
    },
    {
      name: 'Error Handling Pattern',
      code: `
try {
  const data = await fetchUserData(userId);
  if (!data) {
    throw new NotFoundError('User not found');
  }
  return processUserData(data);
} catch (error) {
  logger.error('Failed to process user', { userId, error });
  throw error;
}
      `.trim()
    },
    {
      name: 'React Component',
      code: `
const UserProfile: React.FC<UserProfileProps> = ({ user, onEdit }) => {
  const [isEditing, setIsEditing] = useState(false);

  return (
    <div className="user-profile">
      <h2>{user.name}</h2>
      <p>{user.email}</p>
      {isEditing ? (
        <EditForm user={user} onSave={onEdit} />
      ) : (
        <button onClick={() => setIsEditing(true)}>Edit</button>
      )}
    </div>
  );
};
      `.trim()
    }
  ];

  const models = ['fast', 'code', 'advanced'] as const;
  const results: Record<string, any> = {};

  for (const modelKey of models) {
    console.log(`\n🔧 Testing model: ${modelKey.toUpperCase()}`);

    try {
      const embedder = new MillerEmbedder();
      await embedder.initialize(modelKey);

      const modelResults = {
        modelKey,
        loadTime: 0,
        embeddings: [] as any[],
        avgEmbeddingTime: 0,
        errors: 0
      };

      const embeddingTimes: number[] = [];

      for (const sample of codeSamples) {
        try {
          console.log(`  📝 Embedding: ${sample.name}...`);

          const startTime = Date.now();
          const embedding = await embedder.embedCode(sample.code, {
            file: 'test.ts',
            language: 'typescript',
            layer: 'domain'
          });
          const embeddingTime = Date.now() - startTime;
          embeddingTimes.push(embeddingTime);

          modelResults.embeddings.push({
            name: sample.name,
            dimensions: embedding.dimensions,
            time: embeddingTime,
            model: embedding.model,
            confidence: embedding.confidence
          });

          console.log(`    ✅ ${embeddingTime}ms, ${embedding.dimensions}D, confidence: ${embedding.confidence || 'N/A'}`);

        } catch (error) {
          console.log(`    ❌ Failed: ${error.message}`);
          modelResults.errors++;
        }
      }

      modelResults.avgEmbeddingTime = embeddingTimes.reduce((a, b) => a + b, 0) / embeddingTimes.length;
      results[modelKey] = modelResults;

      console.log(`  📊 Average embedding time: ${Math.round(modelResults.avgEmbeddingTime)}ms`);
      console.log(`  🎯 Errors: ${modelResults.errors}/${codeSamples.length}`);

    } catch (error) {
      console.log(`  ❌ Model initialization failed: ${error.message}`);
      results[modelKey] = { error: error.message };
    }
  }

  // Calculate semantic similarity between similar code patterns
  console.log('\n🔍 Testing Semantic Understanding...');

  // Test semantic similarity for code patterns
  const similarCodePatterns = [
    {
      name: 'Repository Pattern A',
      code: 'class UserRepository { async findById(id) { return db.find(id); } }'
    },
    {
      name: 'Repository Pattern B',
      code: 'class ProductRepository { async getById(id) { return database.findOne(id); } }'
    },
    {
      name: 'Different Pattern',
      code: 'function handleClick() { console.log("clicked"); }'
    }
  ];

  for (const modelKey of models) {
    if (results[modelKey]?.error) continue;

    try {
      console.log(`\n📐 Semantic similarity test - ${modelKey.toUpperCase()}:`);

      const embedder = new MillerEmbedder();
      await embedder.initialize(modelKey);

      const embeddings = await Promise.all(
        similarCodePatterns.map(p => embedder.embedCode(p.code))
      );

      // Calculate cosine similarity between embeddings
      const similarity1 = cosineSimilarity(embeddings[0].vector, embeddings[1].vector);
      const similarity2 = cosineSimilarity(embeddings[0].vector, embeddings[2].vector);

      console.log(`  🔗 Repository A ↔ Repository B: ${(similarity1 * 100).toFixed(1)}%`);
      console.log(`  🔗 Repository A ↔ Click Handler: ${(similarity2 * 100).toFixed(1)}%`);
      console.log(`  📈 Pattern recognition score: ${similarity1 > similarity2 ? '✅ GOOD' : '❌ POOR'}`);

      results[modelKey].semanticScores = {
        similarPatterns: similarity1,
        differentPatterns: similarity2,
        recognitionQuality: similarity1 > similarity2
      };

    } catch (error) {
      console.log(`  ❌ Semantic test failed: ${error.message}`);
    }
  }

  // Summary report
  console.log('\n📊 EMBEDDING MODEL COMPARISON REPORT');
  console.log('='.repeat(50));

  for (const [modelKey, data] of Object.entries(results)) {
    if (data.error) {
      console.log(`\n❌ ${modelKey.toUpperCase()}: FAILED - ${data.error}`);
      continue;
    }

    console.log(`\n✅ ${modelKey.toUpperCase()}:`);
    console.log(`  📏 Dimensions: ${data.embeddings[0]?.dimensions || 'N/A'}`);
    console.log(`  ⚡ Avg Speed: ${Math.round(data.avgEmbeddingTime)}ms per embedding`);
    console.log(`  🎯 Success Rate: ${((codeSamples.length - data.errors) / codeSamples.length * 100).toFixed(0)}%`);

    if (data.semanticScores) {
      console.log(`  🧠 Pattern Recognition: ${data.semanticScores.recognitionQuality ? '✅ Good' : '❌ Poor'}`);
      console.log(`  📈 Similar Code Similarity: ${(data.semanticScores.similarPatterns * 100).toFixed(1)}%`);
    }
  }

  console.log('\n🎯 RECOMMENDATIONS:');

  const fastModel = results['fast'];
  const codeModel = results['code'];

  if (fastModel && codeModel && !fastModel.error && !codeModel.error) {
    const speedDiff = codeModel.avgEmbeddingTime / fastModel.avgEmbeddingTime;
    console.log(`  ⚡ Code model is ${speedDiff.toFixed(1)}x slower than fast model`);

    if (codeModel.semanticScores && fastModel.semanticScores) {
      const qualityDiff = codeModel.semanticScores.similarPatterns / fastModel.semanticScores.similarPatterns;
      console.log(`  🧠 Code model has ${qualityDiff.toFixed(1)}x better pattern recognition`);

      if (qualityDiff > 1.2 && speedDiff < 3) {
        console.log(`  🎯 RECOMMENDED: Switch to 'code' model for better code understanding`);
      } else if (speedDiff > 5) {
        console.log(`  🎯 RECOMMENDED: Keep 'fast' model for better performance`);
      } else {
        console.log(`  🎯 RECOMMENDED: Consider user preference for speed vs quality`);
      }
    }
  }

  console.log('\n🏁 Benchmark Complete!');
}

// Helper function to calculate cosine similarity
function cosineSimilarity(a: Float32Array, b: Float32Array): number {
  if (a.length !== b.length) return 0;

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

benchmarkEmbeddingModels().catch(console.error);