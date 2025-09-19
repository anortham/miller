import { describe, it, expect } from 'bun:test';
import { VueExtractor } from '../../extractors/vue-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('VueExtractor', () => {
  describe('Vue SFC Parsing', () => {
    it('should extract Vue component symbol', () => {
      const vueCode = `
<template>
  <div class="hello-world">
    <h1>{{ message }}</h1>
    <button @click="increment">Count: {{ count }}</button>
  </div>
</template>

<script>
export default {
  name: 'HelloWorld',
  data() {
    return {
      message: 'Hello Vue!',
      count: 0
    }
  },
  methods: {
    increment() {
      this.count++;
    }
  }
}
</script>

<style scoped>
.hello-world {
  padding: 20px;
}
</style>
      `;

      const extractor = new VueExtractor('vue', 'test-component.vue', vueCode);
      const symbols = extractor.extractSymbols(null); // Vue doesn't use tree-sitter

      expect(symbols.length).toBeGreaterThan(0);

      // Check component symbol
      const component = symbols.find(s => s.name === 'HelloWorld');
      expect(component).toBeDefined();
      expect(component?.kind).toBe(SymbolKind.Class);
      expect(component?.signature).toContain('<HelloWorld />');
    });

    it('should extract script section symbols', () => {
      const vueCode = `
<script>
export default {
  data() {
    return { message: 'Hello' }
  },
  computed: {
    upperMessage() {
      return this.message.toUpperCase();
    }
  },
  methods: {
    greet() {
      console.log(this.message);
    },
    calculate(a, b) {
      return a + b;
    }
  }
}
</script>
      `;

      const extractor = new VueExtractor('vue', 'methods.vue', vueCode);
      const symbols = extractor.extractSymbols(null);

      // Should find data, computed, methods, and individual method functions
      const dataSymbol = symbols.find(s => s.name === 'data');
      expect(dataSymbol).toBeDefined();
      expect(dataSymbol?.kind).toBe(SymbolKind.Function);

      const computedSymbol = symbols.find(s => s.name === 'computed');
      expect(computedSymbol).toBeDefined();
      expect(computedSymbol?.kind).toBe(SymbolKind.Property);

      const methodsSymbol = symbols.find(s => s.name === 'methods');
      expect(methodsSymbol).toBeDefined();
      expect(methodsSymbol?.kind).toBe(SymbolKind.Property);

      const greetMethod = symbols.find(s => s.name === 'greet');
      expect(greetMethod).toBeDefined();
      expect(greetMethod?.kind).toBe(SymbolKind.Method);

      const calculateMethod = symbols.find(s => s.name === 'calculate');
      expect(calculateMethod).toBeDefined();
      expect(calculateMethod?.kind).toBe(SymbolKind.Method);
    });

    it('should extract template symbols', () => {
      const vueCode = `
<template>
  <div>
    <UserProfile :user="currentUser" />
    <ProductCard v-for="product in products" :key="product.id" />
    <CustomButton @click="handleClick" v-if="showButton" />
  </div>
</template>
      `;

      const extractor = new VueExtractor('vue', 'template.vue', vueCode);
      const symbols = extractor.extractSymbols(null);

      // Should find component usages
      const userProfile = symbols.find(s => s.name === 'UserProfile');
      expect(userProfile).toBeDefined();
      expect(userProfile?.kind).toBe(SymbolKind.Class);

      const productCard = symbols.find(s => s.name === 'ProductCard');
      expect(productCard).toBeDefined();
      expect(productCard?.kind).toBe(SymbolKind.Class);

      const customButton = symbols.find(s => s.name === 'CustomButton');
      expect(customButton).toBeDefined();
      expect(customButton?.kind).toBe(SymbolKind.Class);

      // Should find directives
      const vFor = symbols.find(s => s.name === 'v-for');
      expect(vFor).toBeDefined();
      expect(vFor?.kind).toBe(SymbolKind.Property);

      const vIf = symbols.find(s => s.name === 'v-if');
      expect(vIf).toBeDefined();
      expect(vIf?.kind).toBe(SymbolKind.Property);
    });

    it('should extract style symbols', () => {
      const vueCode = `
<style scoped>
.container {
  display: flex;
  align-items: center;
}

.button {
  padding: 10px;
  background: blue;
}

.disabled {
  opacity: 0.5;
}
</style>
      `;

      const extractor = new VueExtractor('vue', 'styles.vue', vueCode);
      const symbols = extractor.extractSymbols(null);

      // Should find CSS classes
      const container = symbols.find(s => s.name === 'container');
      expect(container).toBeDefined();
      expect(container?.kind).toBe(SymbolKind.Property);
      expect(container?.signature).toBe('.container');

      const button = symbols.find(s => s.name === 'button');
      expect(button).toBeDefined();
      expect(button?.kind).toBe(SymbolKind.Property);

      const disabled = symbols.find(s => s.name === 'disabled');
      expect(disabled).toBeDefined();
      expect(disabled?.kind).toBe(SymbolKind.Property);
    });

    it('should handle named components', () => {
      const vueCode = `
<script>
export default {
  name: 'MyCustomComponent',
  data() {
    return { value: 42 }
  }
}
</script>
      `;

      const extractor = new VueExtractor('vue', 'custom.vue', vueCode);
      const symbols = extractor.extractSymbols(null);

      // Should use the name from the component, not filename
      const component = symbols.find(s => s.name === 'MyCustomComponent');
      expect(component).toBeDefined();
      expect(component?.kind).toBe(SymbolKind.Class);
    });

    it('should handle complex SFC with all sections', () => {
      const vueCode = `
<template>
  <div class="app">
    <Header :title="pageTitle" />
    <main>
      <slot />
    </main>
  </div>
</template>

<script lang="ts">
export default {
  name: 'AppLayout',
  props: {
    pageTitle: String
  },
  data() {
    return {
      loading: false
    }
  },
  mounted() {
    this.initialize();
  },
  methods: {
    initialize() {
      this.loading = true;
    }
  }
}
</script>

<style lang="scss" scoped>
.app {
  min-height: 100vh;
}

.header {
  position: fixed;
  top: 0;
}
</style>
      `;

      const extractor = new VueExtractor('vue', 'app-layout.vue', vueCode);
      const symbols = extractor.extractSymbols(null);

      expect(symbols.length).toBeGreaterThan(5);

      // Component
      const component = symbols.find(s => s.name === 'AppLayout');
      expect(component).toBeDefined();

      // Script symbols
      expect(symbols.find(s => s.name === 'props')).toBeDefined();
      expect(symbols.find(s => s.name === 'data')).toBeDefined();
      expect(symbols.find(s => s.name === 'methods')).toBeDefined();
      expect(symbols.find(s => s.name === 'initialize')).toBeDefined();

      // Template symbols
      expect(symbols.find(s => s.name === 'Header')).toBeDefined();

      // Style symbols
      expect(symbols.find(s => s.name === 'app')).toBeDefined();
      expect(symbols.find(s => s.name === 'header')).toBeDefined();
    });
  });

  describe('Type Inference', () => {
    it('should return empty types map for now', () => {
      const extractor = new VueExtractor('vue', 'test.vue', '<template></template>');
      const symbols = extractor.extractSymbols(null);
      const types = extractor.inferTypes(symbols);

      expect(types).toBeInstanceOf(Map);
      expect(types.size).toBe(0);
    });
  });

  describe('Relationships', () => {
    it('should return empty relationships for now', () => {
      const extractor = new VueExtractor('vue', 'test.vue', '<template></template>');
      const symbols = extractor.extractSymbols(null);
      const relationships = extractor.extractRelationships(null, symbols);

      expect(relationships).toBeInstanceOf(Array);
      expect(relationships.length).toBe(0);
    });
  });
});