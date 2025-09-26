/**
 * TF-IDF Embedder - Pure JavaScript implementation for Web Workers
 *
 * Generates code embeddings using TF-IDF vectors optimized for code search.
 * This implementation works in Web Workers without any native dependencies,
 * providing immediate semantic search capabilities while we solve transformers.js issues.
 */

/**
 * Code-specific TF-IDF embedder designed for programming languages
 */
export class TFIDFEmbedder {
  constructor(config = {}) {
    this.config = {
      maxFeatures: config.maxFeatures || 1000,
      minDocFreq: config.minDocFreq || 2,
      maxDocFreq: config.maxDocFreq || 0.8,
      ...config
    };

    // Document corpus for IDF calculation
    this.documents = new Map();
    this.vocabulary = new Map(); // term -> index
    this.idf = new Map(); // term -> idf value
    this.documentFreq = new Map(); // term -> document count
    this.totalDocuments = 0;
    this.isBuilt = false;

    // TF-IDF Embedder initialized for code search
  }

  /**
   * Smart tokenization for code
   * Handles camelCase, snake_case, kebab-case, and programming keywords
   */
  tokenize(code) {
    if (!code || typeof code !== 'string') return [];

    const tokens = [];

    // Remove comments and strings first
    const cleanCode = code
      .replace(/\/\*[\s\S]*?\*\//g, ' ') // Block comments
      .replace(/\/\/.*$/gm, ' ')         // Line comments
      .replace(/'([^'\\]|\\.)*'/g, ' ')  // Single quoted strings
      .replace(/"([^"\\]|\\.)*"/g, ' ')  // Double quoted strings
      .replace(/`([^`\\]|\\.)*`/g, ' '); // Template strings

    // Extract different types of tokens
    const patterns = [
      /\b[a-z][a-zA-Z0-9]*\b/g,           // camelCase, lowercase
      /\b[A-Z][a-zA-Z0-9]*\b/g,           // PascalCase, UPPERCASE
      /\b[a-z]+(?:_[a-z]+)+\b/g,          // snake_case
      /\b[a-z]+(?:-[a-z]+)+\b/g,          // kebab-case
      /\b\d+\b/g,                         // Numbers
      /[+\-*/=<>!&|]+/g,                  // Operators
      /[{}[\]();,.:]/g                    // Delimiters
    ];

    patterns.forEach(pattern => {
      const matches = cleanCode.match(pattern);
      if (matches) {
        tokens.push(...matches.map(token => token.toLowerCase()));
      }
    });

    // Split camelCase and PascalCase further
    const expandedTokens = [];
    tokens.forEach(token => {
      // Split camelCase: getUserData -> get, user, data
      const camelSplit = token.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase().split(' ');
      expandedTokens.push(...camelSplit);

      // Keep original token too
      if (camelSplit.length > 1) {
        expandedTokens.push(token);
      }
    });

    // Filter out very short tokens and common noise
    const filtered = expandedTokens.filter(token =>
      token.length >= 2 &&
      !/^[0-9]+$/.test(token) && // Skip pure numbers
      token !== 'var' && token !== 'let' && token !== 'const' // Skip basic keywords
    );

    return [...new Set(filtered)]; // Remove duplicates
  }

  /**
   * Calculate term frequency for a document
   */
  calculateTF(tokens) {
    const tf = new Map();
    const totalTokens = tokens.length;

    if (totalTokens === 0) return tf;

    for (const token of tokens) {
      tf.set(token, (tf.get(token) || 0) + 1);
    }

    // Normalize by document length
    for (const [term, count] of tf) {
      tf.set(term, count / totalTokens);
    }

    return tf;
  }

  /**
   * Add a document to the corpus for IDF calculation
   */
  addDocument(id, code) {
    const tokens = this.tokenize(code);
    this.documents.set(id, tokens);

    // Update document frequency counts
    const uniqueTokens = new Set(tokens);
    for (const token of uniqueTokens) {
      this.documentFreq.set(token, (this.documentFreq.get(token) || 0) + 1);
    }

    this.totalDocuments++;
    this.isBuilt = false; // Need to rebuild vocabulary
  }

  /**
   * Build vocabulary and calculate IDF values
   */
  buildVocabulary() {
    if (this.totalDocuments === 0) {
      // No documents added to corpus
      return;
    }

    // Filter terms by document frequency
    const filteredTerms = [];
    const minDocCount = Math.max(this.config.minDocFreq, 1);
    const maxDocCount = Math.floor(this.totalDocuments * this.config.maxDocFreq);

    for (const [term, docCount] of this.documentFreq) {
      if (docCount >= minDocCount && docCount <= maxDocCount) {
        filteredTerms.push([term, docCount]);
      }
    }

    // Sort by document frequency and take top features
    filteredTerms.sort((a, b) => b[1] - a[1]);
    const topTerms = filteredTerms.slice(0, this.config.maxFeatures);

    // Build vocabulary mapping
    this.vocabulary.clear();
    this.idf.clear();

    topTerms.forEach(([term, docCount], index) => {
      this.vocabulary.set(term, index);
      // IDF = log(total_docs / doc_freq)
      this.idf.set(term, Math.log(this.totalDocuments / docCount));
    });

    this.isBuilt = true;
    // TF-IDF vocabulary built: {this.vocabulary.size} terms from {this.totalDocuments} documents
  }

  /**
   * Generate TF-IDF embedding for code
   */
  embed(code) {
    if (!this.isBuilt) {
      this.buildVocabulary();
    }

    const tokens = this.tokenize(code);
    const tf = this.calculateTF(tokens);

    // Create sparse vector
    const vector = new Float32Array(this.vocabulary.size);

    for (const [term, tfValue] of tf) {
      const index = this.vocabulary.get(term);
      if (index !== undefined) {
        const idfValue = this.idf.get(term) || 0;
        vector[index] = tfValue * idfValue;
      }
    }

    return {
      vector: Array.from(vector),  // Convert Float32Array to regular array for consistency
      dimensions: this.vocabulary.size,
      model: 'tfidf-code-v1',
      timestamp: Date.now(),
      confidence: this.calculateConfidence(tokens)
    };
  }

  /**
   * Calculate confidence based on token coverage
   */
  calculateConfidence(tokens) {
    if (tokens.length === 0 || this.vocabulary.size === 0) return 0.0;

    const knownTokens = tokens.filter(token => this.vocabulary.has(token));
    return Math.min(1.0, knownTokens.length / Math.max(tokens.length, 5));
  }

  /**
   * Generate embedding for query (alias for embed method)
   * Required by hybrid search engine interface
   */
  async embedQuery(query, context) {
    return this.embed(query);
  }

  /**
   * Generate embedding for code (alias for embed method)
   * Required by enhanced code intelligence interface
   */
  async embedCode(code, context) {
    return this.embed(code);
  }

  /**
   * Initialize the embedder (compatibility method)
   * TF-IDF doesn't require async initialization
   */
  async initialize() {
    // TF-IDF is ready immediately, no async setup needed
    return Promise.resolve();
  }

  /**
   * Batch embedding generation
   */
  embedBatch(codeSnippets) {
    return codeSnippets.map(code => this.embed(code));
  }

  /**
   * Calculate cosine similarity between two vectors
   */
  cosineSimilarity(vec1, vec2) {
    if (vec1.length !== vec2.length) return 0;

    let dotProduct = 0;
    let norm1 = 0;
    let norm2 = 0;

    for (let i = 0; i < vec1.length; i++) {
      dotProduct += vec1[i] * vec2[i];
      norm1 += vec1[i] * vec1[i];
      norm2 += vec2[i] * vec2[i];
    }

    if (norm1 === 0 || norm2 === 0) return 0;
    return dotProduct / (Math.sqrt(norm1) * Math.sqrt(norm2));
  }

  /**
   * Search similar code snippets
   */
  search(query, topK = 10) {
    const queryEmbedding = this.embed(query);
    const results = [];

    for (const [docId, tokens] of this.documents) {
      const docCode = tokens.join(' '); // Reconstruct for embedding
      const docEmbedding = this.embed(docCode);
      const similarity = this.cosineSimilarity(queryEmbedding.vector, docEmbedding.vector);

      results.push({ id: docId, similarity, code: docCode });
    }

    return results
      .sort((a, b) => b.similarity - a.similarity)
      .slice(0, topK);
  }

  /**
   * Get embedding statistics
   */
  getStats() {
    return {
      totalDocuments: this.totalDocuments,
      vocabularySize: this.vocabulary.size,
      maxFeatures: this.config.maxFeatures,
      isBuilt: this.isBuilt,
      model: 'tfidf-code-v1'
    };
  }

  /**
   * Clear all data
   */
  clear() {
    this.documents.clear();
    this.vocabulary.clear();
    this.idf.clear();
    this.documentFreq.clear();
    this.totalDocuments = 0;
    this.isBuilt = false;
    console.log('🧹 TF-IDF embedder cleared');
  }
}