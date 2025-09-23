import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { CSSExtractor } from '../../extractors/css-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('CSSExtractor', () => {
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

  describe('Basic CSS Selectors and Rules', () => {
    it('should extract basic selectors, properties, and values', async () => {
      const cssCode = `
/* Global styles */
* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

/* Element selectors */
body {
  font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
  line-height: 1.6;
  color: #333;
  background-color: #ffffff;
}

h1, h2, h3, h4, h5, h6 {
  font-weight: bnew;
  margin-bottom: 1rem;
  color: #2c3e50;
}

a {
  color: #3498db;
  text-decoration: none;
  transition: color 0.3s ease;
}

a:hover {
  color: #2980b9;
  text-decoration: underline;
}

a:visited {
  color: #8e44ad;
}

a:active {
  color: #e74c3c;
}

/* Class selectors */
.container {
  max-width: 1200px;
  margin: 0 auto;
  padding: 0 2rem;
}

.header {
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
  padding: 2rem 0;
  text-align: center;
}

.navigation {
  background-color: rgba(255, 255, 255, 0.95);
  backdrop-filter: blur(10px);
  position: sticky;
  top: 0;
  z-index: 1000;
}

.btn {
  display: inline-block;
  padding: 0.75rem 1.5rem;
  border: none;
  border-radius: 4px;
  font-size: 1rem;
  cursor: pointer;
  transition: all 0.3s ease;
}

.btn-primary {
  background-color: #3498db;
  color: white;
}

.btn-primary:hover {
  background-color: #2980b9;
  transform: translateY(-2px);
  box-shadow: 0 4px 8px rgba(52, 152, 219, 0.3);
}

.btn-secondary {
  background-color: #95a5a6;
  color: white;
}

/* ID selectors */
#main-content {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
}

#footer {
  background-color: #34495e;
  color: #ecf0f1;
  text-align: center;
  padding: 2rem 0;
  margin-top: auto;
}

/* Attribute selectors */
[data-theme="dark"] {
  background-color: #2c3e50;
  color: #ecf0f1;
}

[data-component="modal"] {
  position: fixed;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  background-color: rgba(0, 0, 0, 0.8);
  display: flex;
  align-items: center;
  justify-content: center;
}

input[type="text"],
input[type="email"],
input[type="password"] {
  width: 100%;
  padding: 0.75rem;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 1rem;
}

input[type="text"]:focus,
input[type="email"]:focus,
input[type="password"]:focus {
  outline: none;
  border-color: #3498db;
  box-shadow: 0 0 0 3px rgba(52, 152, 219, 0.1);
}

button[disabled] {
  opacity: 0.6;
  cursor: not-allowed;
}
`;

      const result = await parserManager.parseFile('basic.css', cssCode);
      const extractor = new CSSExtractor('css', 'basic.css', cssCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Universal selector
      const universalSelector = symbols.find(s => s.name === '*');
      expect(universalSelector).toBeDefined();
      expect(universalSelector?.kind).toBe(SymbolKind.Variable); // CSS rules as variables

      // Element selectors
      const bodySelector = symbols.find(s => s.name === 'body');
      expect(bodySelector).toBeDefined();
      expect(bodySelector?.signature).toContain('font-family');

      const headingSelectors = symbols.find(s => s.name === 'h1, h2, h3, h4, h5, h6');
      expect(headingSelectors).toBeDefined();
      expect(headingSelectors?.signature).toContain('font-weight: bnew');

      // Anchor pseudo-classes
      const anchorHover = symbols.find(s => s.name === 'a:hover');
      expect(anchorHover).toBeDefined();
      expect(anchorHover?.signature).toContain('text-decoration: underline');

      const anchorVisited = symbols.find(s => s.name === 'a:visited');
      expect(anchorVisited).toBeDefined();

      const anchorActive = symbols.find(s => s.name === 'a:active');
      expect(anchorActive).toBeDefined();

      // Class selectors
      const containerClass = symbols.find(s => s.name === '.container');
      expect(containerClass).toBeDefined();
      expect(containerClass?.signature).toContain('max-width: 1200px');

      const headerClass = symbols.find(s => s.name === '.header');
      expect(headerClass).toBeDefined();
      expect(headerClass?.signature).toContain('linear-gradient');

      const navigationClass = symbols.find(s => s.name === '.navigation');
      expect(navigationClass).toBeDefined();
      expect(navigationClass?.signature).toContain('backdrop-filter: blur(10px)');

      // Button classes
      const btnClass = symbols.find(s => s.name === '.btn');
      expect(btnClass).toBeDefined();
      expect(btnClass?.signature).toContain('display: inline-block');

      const btnPrimary = symbols.find(s => s.name === '.btn-primary');
      expect(btnPrimary).toBeDefined();

      const btnPrimaryHover = symbols.find(s => s.name === '.btn-primary:hover');
      expect(btnPrimaryHover).toBeDefined();
      expect(btnPrimaryHover?.signature).toContain('transform: translateY(-2px)');

      // ID selectors
      const mainContent = symbols.find(s => s.name === '#main-content');
      expect(mainContent).toBeDefined();
      expect(mainContent?.signature).toContain('display: flex');

      const footer = symbols.find(s => s.name === '#footer');
      expect(footer).toBeDefined();
      expect(footer?.signature).toContain('margin-top: auto');

      // Attribute selectors
      const darkTheme = symbols.find(s => s.name === '[data-theme="dark"]');
      expect(darkTheme).toBeDefined();
      expect(darkTheme?.signature).toContain('background-color: #2c3e50');

      const modalComponent = symbols.find(s => s.name === '[data-component="modal"]');
      expect(modalComponent).toBeDefined();
      expect(modalComponent?.signature).toContain('position: fixed');

      // Input type selectors
      const textInputs = symbols.find(s => s.name?.includes('input[type="text"]'));
      expect(textInputs).toBeDefined();
      expect(textInputs?.signature).toContain('width: 100%');

      const inputFocus = symbols.find(s => s.name?.includes('input[type="text"]:focus'));
      expect(inputFocus).toBeDefined();
      expect(inputFocus?.signature).toContain('box-shadow');

      const disabledButton = symbols.find(s => s.name === 'button[disabled]');
      expect(disabledButton).toBeDefined();
      expect(disabledButton?.signature).toContain('cursor: not-allowed');
    });
  });

  describe('Modern CSS Features and Layout Systems', () => {
    it('should extract CSS Grid, Flexbox, and modern layout properties', async () => {
      const cssCode = `
/* CSS Grid Layout */
.grid-container {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  grid-template-rows: auto 1fr auto;
  grid-template-areas:
    "header header header"
    "sidebar main aside"
    "footer footer footer";
  gap: 2rem;
  min-height: 100vh;
}

.grid-header {
  grid-area: header;
  background-color: #3498db;
  padding: 2rem;
}

.grid-sidebar {
  grid-area: sidebar;
  background-color: #ecf0f1;
  padding: 1rem;
}

.grid-main {
  grid-area: main;
  padding: 2rem;
}

.grid-aside {
  grid-area: aside;
  background-color: #f8f9fa;
  padding: 1rem;
}

.grid-footer {
  grid-area: footer;
  background-color: #2c3e50;
  color: white;
  padding: 2rem;
  text-align: center;
}

/* Advanced Grid */
.photo-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(250px, 1fr));
  grid-auto-rows: 200px;
  grid-auto-flow: dense;
  gap: 1rem;
}

.photo-item:nth-child(3n) {
  grid-column: span 2;
  grid-row: span 2;
}

.photo-item:nth-child(5n) {
  grid-column: span 1;
  grid-row: span 3;
}

/* CSS Flexbox */
.flex-container {
  display: flex;
  flex-direction: row;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  align-content: flex-start;
  gap: 1rem;
}

.flex-item {
  flex: 1 1 auto;
  min-width: 0;
}

.flex-item-grow {
  flex-grow: 2;
  flex-shrink: 1;
  flex-basis: 200px;
}

.flex-item-fixed {
  flex: 0 0 150px;
}

/* Flexbox Navigation */
.nav-flex {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1rem 2rem;
}

.nav-flex .logo {
  flex: 0 0 auto;
}

.nav-flex .menu {
  display: flex;
  list-style: none;
  margin: 0;
  padding: 0;
  gap: 2rem;
}

.nav-flex .actions {
  display: flex;
  gap: 1rem;
  margin-left: auto;
}

/* Modern Positioning */
.sticky-header {
  position: sticky;
  top: 0;
  z-index: 100;
  background-color: rgba(255, 255, 255, 0.95);
  backdrop-filter: blur(10px);
}

.fixed-sidebar {
  position: fixed;
  top: 80px;
  left: 0;
  width: 250px;
  height: calc(100vh - 80px);
  overflow-y: auto;
  background-color: #f8f9fa;
  border-right: 1px solid #dee2e6;
}

.absolute-overlay {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  background-color: white;
  padding: 2rem;
  border-radius: 8px;
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.2);
}

/* Subgrid (newer CSS feature) */
.subgrid-container {
  display: grid;
  grid-template-columns: 1fr 2fr 1fr;
  gap: 1rem;
}

.subgrid-item {
  display: grid;
  grid-template-columns: subgrid;
  grid-column: 1 / -1;
  gap: inherit;
}

/* Container Queries */
.card-container {
  container-type: inline-size;
  container-name: card;
}

@container card (min-width: 400px) {
  .card-content {
    display: flex;
    gap: 1rem;
  }

  .card-image {
    flex: 0 0 150px;
  }
}

@container card (min-width: 600px) {
  .card-content {
    flex-direction: column;
  }

  .card-image {
    flex: none;
    width: 100%;
    height: 200px;
  }
}
`;

      const result = await parserManager.parseFile('layout.css', cssCode);
      const extractor = new CSSExtractor('css', 'layout.css', cssCode);
      const symbols = extractor.extractSymbols(result.tree);

      // CSS Grid container
      const gridContainer = symbols.find(s => s.name === '.grid-container');
      expect(gridContainer).toBeDefined();
      expect(gridContainer?.signature).toContain('display: grid');
      expect(gridContainer?.signature).toContain('grid-template-columns: repeat(auto-fit, minmax(300px, 1fr))');
      expect(gridContainer?.signature).toContain('grid-template-areas');

      // Grid areas
      const gridHeader = symbols.find(s => s.name === '.grid-header');
      expect(gridHeader).toBeDefined();
      expect(gridHeader?.signature).toContain('grid-area: header');

      const gridSidebar = symbols.find(s => s.name === '.grid-sidebar');
      expect(gridSidebar).toBeDefined();
      expect(gridSidebar?.signature).toContain('grid-area: sidebar');

      const gridMain = symbols.find(s => s.name === '.grid-main');
      expect(gridMain).toBeDefined();
      expect(gridMain?.signature).toContain('grid-area: main');

      // Advanced grid
      const photoGrid = symbols.find(s => s.name === '.photo-grid');
      expect(photoGrid).toBeDefined();
      expect(photoGrid?.signature).toContain('grid-auto-flow: dense');

      const photoItemNth = symbols.find(s => s.name === '.photo-item:nth-child(3n)');
      expect(photoItemNth).toBeDefined();
      expect(photoItemNth?.signature).toContain('grid-column: span 2');

      // Flexbox container
      const flexContainer = symbols.find(s => s.name === '.flex-container');
      expect(flexContainer).toBeDefined();
      expect(flexContainer?.signature).toContain('display: flex');
      expect(flexContainer?.signature).toContain('justify-content: space-between');
      expect(flexContainer?.signature).toContain('align-items: center');

      // Flex items
      const flexItem = symbols.find(s => s.name === '.flex-item');
      expect(flexItem).toBeDefined();
      expect(flexItem?.signature).toContain('flex: 1 1 auto');

      const flexItemGrow = symbols.find(s => s.name === '.flex-item-grow');
      expect(flexItemGrow).toBeDefined();
      expect(flexItemGrow?.signature).toContain('flex-grow: 2');

      const flexItemFixed = symbols.find(s => s.name === '.flex-item-fixed');
      expect(flexItemFixed).toBeDefined();
      expect(flexItemFixed?.signature).toContain('flex: 0 0 150px');

      // Navigation flex
      const navFlex = symbols.find(s => s.name === '.nav-flex');
      expect(navFlex).toBeDefined();

      const navFlexLogo = symbols.find(s => s.name === '.nav-flex .logo');
      expect(navFlexLogo).toBeDefined();

      const navFlexMenu = symbols.find(s => s.name === '.nav-flex .menu');
      expect(navFlexMenu).toBeDefined();

      // Modern positioning
      const stickyHeader = symbols.find(s => s.name === '.sticky-header');
      expect(stickyHeader).toBeDefined();
      expect(stickyHeader?.signature).toContain('position: sticky');
      expect(stickyHeader?.signature).toContain('backdrop-filter: blur(10px)');

      const fixedSidebar = symbols.find(s => s.name === '.fixed-sidebar');
      expect(fixedSidebar).toBeDefined();
      expect(fixedSidebar?.signature).toContain('position: fixed');
      expect(fixedSidebar?.signature).toContain('height: calc(100vh - 80px)');

      const absoluteOverlay = symbols.find(s => s.name === '.absolute-overlay');
      expect(absoluteOverlay).toBeDefined();
      expect(absoluteOverlay?.signature).toContain('transform: translate(-50%, -50%)');

      // Subgrid
      const subgridContainer = symbols.find(s => s.name === '.subgrid-container');
      expect(subgridContainer).toBeDefined();

      const subgridItem = symbols.find(s => s.name === '.subgrid-item');
      expect(subgridItem).toBeDefined();
      expect(subgridItem?.signature).toContain('grid-template-columns: subgrid');

      // Container queries
      const cardContainer = symbols.find(s => s.name === '.card-container');
      expect(cardContainer).toBeDefined();
      expect(cardContainer?.signature).toContain('container-type: inline-size');

      // Container query rules
      const containerQuery400 = symbols.find(s => s.signature?.includes('@container card (min-width: 400px)'));
      expect(containerQuery400).toBeDefined();

      const containerQuery600 = symbols.find(s => s.signature?.includes('@container card (min-width: 600px)'));
      expect(containerQuery600).toBeDefined();
    });
  });

  describe('CSS Custom Properties and Functions', () => {
    it('should extract CSS variables, calc(), and modern CSS functions', async () => {
      const cssCode = `
/* CSS Custom Properties (Variables) */
:root {
  /* Color palette */
  --primary-color: #3498db;
  --secondary-color: #2ecc71;
  --accent-color: #e74c3c;
  --background-color: #ffffff;
  --text-color: #2c3e50;
  --border-color: #bdc3c7;

  /* Typography */
  --font-family-primary: 'Inter', system-ui, sans-serif;
  --font-family-secondary: 'Fira Code', 'Courier New', monospace;
  --font-size-base: 1rem;
  --font-size-small: 0.875rem;
  --font-size-large: 1.125rem;
  --line-height-base: 1.6;

  /* Spacing */
  --spacing-xs: 0.25rem;
  --spacing-sm: 0.5rem;
  --spacing-md: 1rem;
  --spacing-lg: 1.5rem;
  --spacing-xl: 2rem;
  --spacing-2xl: 3rem;

  /* Layout */
  --container-max-width: 1200px;
  --sidebar-width: 250px;
  --header-height: 80px;
  --border-radius: 8px;

  /* Animations */
  --transition-fast: 0.15s ease;
  --transition-base: 0.3s ease;
  --transition-slow: 0.5s ease;

  /* Z-index scale */
  --z-dropdown: 1000;
  --z-sticky: 1020;
  --z-fixed: 1030;
  --z-modal: 1040;
  --z-tooltip: 1050;
}

/* Dark theme variables */
[data-theme="dark"] {
  --primary-color: #5dade2;
  --background-color: #2c3e50;
  --text-color: #ecf0f1;
  --border-color: #34495e;
}

/* Component using custom properties */
.button {
  background-color: var(--primary-color);
  color: var(--background-color);
  font-family: var(--font-family-primary);
  font-size: var(--font-size-base);
  padding: var(--spacing-sm) var(--spacing-md);
  border: 1px solid var(--border-color, transparent);
  border-radius: var(--border-radius);
  transition: var(--transition-base);
  cursor: pointer;
}

.button:hover {
  background-color: var(--primary-color, #3498db);
  transform: translateY(-2px);
  box-shadow: 0 4px 12px rgba(var(--primary-color), 0.3);
}

/* CSS calc() and mathematical functions */
.layout-calc {
  width: calc(100% - var(--sidebar-width));
  height: calc(100vh - var(--header-height));
  margin-left: calc(var(--spacing-md) * 2);
  padding: calc(var(--spacing-base) + 10px);
}

.responsive-grid {
  grid-template-columns: repeat(auto-fit, minmax(calc(300px + 2rem), 1fr));
  gap: calc(var(--spacing-md) + var(--spacing-sm));
}

.complex-calc {
  width: calc((100vw - var(--sidebar-width)) / 3 - var(--spacing-lg));
  height: calc(100vh - var(--header-height) - var(--spacing-xl) * 2);
  font-size: calc(var(--font-size-base) + 0.5vw);
}

/* Modern CSS functions */
.modern-functions {
  /* Clamp for fluid typography */
  font-size: clamp(1rem, 2.5vw, 2rem);

  /* Min/Max functions */
  width: min(90vw, var(--container-max-width));
  height: max(50vh, 400px);

  /* Color functions */
  background-color: hsl(var(--primary-hue, 200), 70%, 50%);
  color: oklch(0.7 0.15 200);

  /* New viewport units */
  min-height: 100svh; /* Small viewport height */
  padding-top: max(env(safe-area-inset-top), var(--spacing-md));
}

/* CSS comparison functions */
.comparison-functions {
  margin: max(var(--spacing-md), 2rem);
  padding: min(5%, var(--spacing-xl));
  width: clamp(300px, 50vw, 800px);
  height: clamp(200px, 30vh, 500px);
}

/* Trigonometric and exponential functions */
.advanced-math {
  /* CSS sin/cos/tan (experimental) */
  transform: rotate(calc(sin(var(--angle, 0)) * 45deg));

  /* Exponential functions */
  opacity: pow(0.8, var(--depth, 1));

  /* Square root */
  border-radius: calc(sqrt(var(--area, 100)) * 1px);
}

/* CSS logical properties with custom properties */
.logical-properties {
  margin-inline: var(--spacing-md);
  margin-block: var(--spacing-sm);
  padding-inline-start: var(--spacing-lg);
  border-inline-end: 1px solid var(--border-color);
  inset-inline-start: var(--sidebar-width);
}

/* CSS nesting with custom properties */
.nested-component {
  background-color: var(--background-color);
  border: 1px solid var(--border-color);

  & .header {
    background-color: var(--primary-color);
    padding: var(--spacing-sm);

    & h2 {
      color: var(--background-color);
      font-size: var(--font-size-large);
    }
  }

  & .content {
    padding: var(--spacing-md);

    & p {
      color: var(--text-color);
      line-height: var(--line-height-base);
    }
  }
}

/* CSS color-mix function */
.color-mixing {
  background-color: color-mix(in oklch, var(--primary-color), var(--secondary-color) 30%);
  border-color: color-mix(in srgb, var(--border-color), transparent 50%);
}

/* CSS container query units with custom properties */
.container-units {
  width: calc(50cqw - var(--spacing-md));
  height: calc(30cqh + var(--spacing-lg));
  font-size: calc(2cqi + var(--font-size-base));
}
`;

      const result = await parserManager.parseFile('variables.css', cssCode);
      const extractor = new CSSExtractor('css', 'variables.css', cssCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Root variables
      const rootSelector = symbols.find(s => s.name === ':root');
      expect(rootSelector).toBeDefined();
      expect(rootSelector?.signature).toContain('--primary-color: #3498db');
      expect(rootSelector?.signature).toContain('--font-family-primary');
      expect(rootSelector?.signature).toContain('--spacing-xs: 0.25rem');

      // Theme variables
      const darkTheme = symbols.find(s => s.name === '[data-theme="dark"]');
      expect(darkTheme).toBeDefined();
      expect(darkTheme?.signature).toContain('--primary-color: #5dade2');

      // Components using variables
      const button = symbols.find(s => s.name === '.button');
      expect(button).toBeDefined();
      expect(button?.signature).toContain('background-color: var(--primary-color)');
      expect(button?.signature).toContain('padding: var(--spacing-sm) var(--spacing-md)');

      const buttonHover = symbols.find(s => s.name === '.button:hover');
      expect(buttonHover).toBeDefined();
      expect(buttonHover?.signature).toContain('var(--primary-color, #3498db)');

      // Calc functions
      const layoutCalc = symbols.find(s => s.name === '.layout-calc');
      expect(layoutCalc).toBeDefined();
      expect(layoutCalc?.signature).toContain('width: calc(100% - var(--sidebar-width))');
      expect(layoutCalc?.signature).toContain('height: calc(100vh - var(--header-height))');

      const responsiveGrid = symbols.find(s => s.name === '.responsive-grid');
      expect(responsiveGrid).toBeDefined();
      expect(responsiveGrid?.signature).toContain('calc(300px + 2rem)');

      const complexCalc = symbols.find(s => s.name === '.complex-calc');
      expect(complexCalc).toBeDefined();
      expect(complexCalc?.signature).toContain('calc((100vw - var(--sidebar-width)) / 3');

      // Modern CSS functions
      const modernFunctions = symbols.find(s => s.name === '.modern-functions');
      expect(modernFunctions).toBeDefined();
      expect(modernFunctions?.signature).toContain('clamp(1rem, 2.5vw, 2rem)');
      expect(modernFunctions?.signature).toContain('min(90vw, var(--container-max-width))');
      expect(modernFunctions?.signature).toContain('max(50vh, 400px)');
      expect(modernFunctions?.signature).toContain('oklch(0.7 0.15 200)');
      expect(modernFunctions?.signature).toContain('100svh');

      // Comparison functions
      const comparisonFunctions = symbols.find(s => s.name === '.comparison-functions');
      expect(comparisonFunctions).toBeDefined();
      expect(comparisonFunctions?.signature).toContain('max(var(--spacing-md), 2rem)');
      expect(comparisonFunctions?.signature).toContain('clamp(300px, 50vw, 800px)');

      // Advanced math functions
      const advancedMath = symbols.find(s => s.name === '.advanced-math');
      expect(advancedMath).toBeDefined();
      expect(advancedMath?.signature).toContain('sin(var(--angle, 0))');
      expect(advancedMath?.signature).toContain('pow(0.8, var(--depth, 1))');
      expect(advancedMath?.signature).toContain('sqrt(var(--area, 100))');

      // Logical properties
      const logicalProperties = symbols.find(s => s.name === '.logical-properties');
      expect(logicalProperties).toBeDefined();
      expect(logicalProperties?.signature).toContain('margin-inline: var(--spacing-md)');
      expect(logicalProperties?.signature).toContain('inset-inline-start: var(--sidebar-width)');

      // CSS nesting
      const nestedComponent = symbols.find(s => s.name === '.nested-component');
      expect(nestedComponent).toBeDefined();

      const nestedHeader = symbols.find(s => s.name === '& .header' || s.name?.includes('.nested-component .header'));
      expect(nestedHeader).toBeDefined();

      // Color mixing
      const colorMixing = symbols.find(s => s.name === '.color-mixing');
      expect(colorMixing).toBeDefined();
      expect(colorMixing?.signature).toContain('color-mix(in oklch');

      // Container units
      const containerUnits = symbols.find(s => s.name === '.container-units');
      expect(containerUnits).toBeDefined();
      expect(containerUnits?.signature).toContain('50cqw');
      expect(containerUnits?.signature).toContain('30cqh');
      expect(containerUnits?.signature).toContain('2cqi');
    });
  });

  describe('CSS At-Rules and Media Queries', () => {
    it('should extract @media, @keyframes, @import, and other at-rules', async () => {
      const cssCode = `
/* CSS imports */
@import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap');
@import url('./normalize.css');
@import url('./components.css') layer(components);

/* CSS layers */
@layer reset, base, components, utilities;

@layer reset {
  * {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
  }
}

@layer base {
  body {
    font-family: 'Inter', system-ui, sans-serif;
    line-height: 1.6;
  }
}

/* Media queries */
@media (min-width: 768px) {
  .container {
    max-width: 750px;
  }

  .grid {
    grid-template-columns: repeat(2, 1fr);
  }

  .navigation {
    flex-direction: row;
  }
}

@media (min-width: 1024px) {
  .container {
    max-width: 980px;
  }

  .grid {
    grid-template-columns: repeat(3, 1fr);
  }

  .sidebar {
    display: block;
  }
}

@media (min-width: 1200px) {
  .container {
    max-width: 1140px;
  }

  .grid {
    grid-template-columns: repeat(4, 1fr);
  }
}

/* Feature queries */
@supports (display: grid) {
  .layout {
    display: grid;
    grid-template-columns: 1fr 3fr 1fr;
  }
}

@supports (backdrop-filter: blur(10px)) {
  .modal-backdrop {
    backdrop-filter: blur(10px);
    background-color: rgba(0, 0, 0, 0.5);
  }
}

@supports not (display: grid) {
  .layout {
    display: flex;
    flex-wrap: wrap;
  }

  .layout > * {
    flex: 1 1 300px;
  }
}

/* Keyframe animations */
@keyframes fadeIn {
  from {
    opacity: 0;
    transform: translateY(20px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@keyframes slideInFromLeft {
  0% {
    transform: translateX(-100%);
    opacity: 0;
  }
  50% {
    opacity: 0.5;
  }
  100% {
    transform: translateX(0);
    opacity: 1;
  }
}

@keyframes bounceIn {
  0% {
    transform: scale(0.3);
    opacity: 0;
  }
  50% {
    transform: scale(1.05);
    opacity: 0.8;
  }
  70% {
    transform: scale(0.9);
    opacity: 0.9;
  }
  100% {
    transform: scale(1);
    opacity: 1;
  }
}

@keyframes pulse {
  0%, 100% {
    transform: scale(1);
    opacity: 1;
  }
  50% {
    transform: scale(1.1);
    opacity: 0.7;
  }
}

/* Complex media queries */
@media screen and (min-width: 768px) and (max-width: 1023px) {
  .tablet-only {
    display: block;
  }
}

@media (orientation: landscape) and (max-height: 500px) {
  .landscape-short {
    height: 100vh;
    overflow-y: auto;
  }
}

@media (prefers-color-scheme: dark) {
  :root {
    --background-color: #1a1a1a;
    --text-color: #ffffff;
  }
}

@media (prefers-reduced-motion: reduce) {
  * {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
  }
}

@media (hover: hover) and (pointer: fine) {
  .hover-effects:hover {
    transform: scale(1.05);
    box-shadow: 0 10px 20px rgba(0, 0, 0, 0.2);
  }
}

/* Print styles */
@media print {
  .no-print {
    display: none !important;
  }

  .container {
    max-width: none;
    margin: 0;
    padding: 0;
  }

  body {
    font-size: 12pt;
    line-height: 1.4;
  }

  h1, h2, h3 {
    page-break-after: avoid;
  }

  img {
    max-width: 100% !important;
    height: auto !important;
  }
}

/* Font face declarations */
@font-face {
  font-family: 'CustomFont';
  src: url('./fonts/custom-font.woff2') format('woff2'),
       url('./fonts/custom-font.woff') format('woff');
  font-weight: 400;
  font-style: normal;
  font-display: swap;
}

@font-face {
  font-family: 'CustomFont';
  src: url('./fonts/custom-font-bnew.woff2') format('woff2');
  font-weight: 700;
  font-style: normal;
  font-display: swap;
}

/* CSS custom properties in media queries */
@media (min-width: 768px) {
  :root {
    --container-padding: 2rem;
    --font-size-base: 1.125rem;
    --grid-columns: 2;
  }
}

@media (min-width: 1024px) {
  :root {
    --container-padding: 3rem;
    --font-size-base: 1.25rem;
    --grid-columns: 3;
  }
}

/* Animation classes using keyframes */
.fade-in {
  animation: fadeIn 0.6s ease-out;
}

.slide-in-left {
  animation: slideInFromLeft 0.8s cubic-bezier(0.25, 0.46, 0.45, 0.94);
}

.bounce-in {
  animation: bounceIn 1s ease-out;
}

.pulse-animation {
  animation: pulse 2s ease-in-out infinite;
}

.complex-animation {
  animation:
    fadeIn 0.5s ease-out,
    slideInFromLeft 0.8s 0.2s ease-out both,
    pulse 2s 1s ease-in-out infinite;
}
`;

      const result = await parserManager.parseFile('at-rules.css', cssCode);
      const extractor = new CSSExtractor('css', 'at-rules.css', cssCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Import statements
      const fontImport = symbols.find(s => s.signature?.includes('@import url(\'https://fonts.googleapis.com'));
      expect(fontImport).toBeDefined();
      expect(fontImport?.kind).toBe(SymbolKind.Import);

      const normalizeImport = symbols.find(s => s.signature?.includes('@import url(\'./normalize.css\')'));
      expect(normalizeImport).toBeDefined();

      const layeredImport = symbols.find(s => s.signature?.includes('layer(components)'));
      expect(layeredImport).toBeDefined();

      // CSS layers
      const layerDeclaration = symbols.find(s => s.signature?.includes('@layer reset, base, components'));
      expect(layerDeclaration).toBeDefined();

      const resetLayer = symbols.find(s => s.signature?.includes('@layer reset'));
      expect(resetLayer).toBeDefined();

      const baseLayer = symbols.find(s => s.signature?.includes('@layer base'));
      expect(baseLayer).toBeDefined();

      // Media queries
      const media768 = symbols.find(s => s.signature?.includes('@media (min-width: 768px)'));
      expect(media768).toBeDefined();

      const media1024 = symbols.find(s => s.signature?.includes('@media (min-width: 1024px)'));
      expect(media1024).toBeDefined();

      const media1200 = symbols.find(s => s.signature?.includes('@media (min-width: 1200px)'));
      expect(media1200).toBeDefined();

      // Feature queries
      const gridSupport = symbols.find(s => s.signature?.includes('@supports (display: grid)'));
      expect(gridSupport).toBeDefined();

      const backdropSupport = symbols.find(s => s.signature?.includes('@supports (backdrop-filter: blur(10px))'));
      expect(backdropSupport).toBeDefined();

      const noGridSupport = symbols.find(s => s.signature?.includes('@supports not (display: grid)'));
      expect(noGridSupport).toBeDefined();

      // Keyframes
      const fadeInKeyframes = symbols.find(s => s.signature?.includes('@keyframes fadeIn'));
      expect(fadeInKeyframes).toBeDefined();
      expect(fadeInKeyframes?.kind).toBe(SymbolKind.Function); // Animations as functions

      const slideInKeyframes = symbols.find(s => s.signature?.includes('@keyframes slideInFromLeft'));
      expect(slideInKeyframes).toBeDefined();

      const bounceInKeyframes = symbols.find(s => s.signature?.includes('@keyframes bounceIn'));
      expect(bounceInKeyframes).toBeDefined();

      const pulseKeyframes = symbols.find(s => s.signature?.includes('@keyframes pulse'));
      expect(pulseKeyframes).toBeDefined();

      // Complex media queries
      const tabletOnly = symbols.find(s => s.signature?.includes('(min-width: 768px) and (max-width: 1023px)'));
      expect(tabletOnly).toBeDefined();

      const landscapeShort = symbols.find(s => s.signature?.includes('(orientation: landscape) and (max-height: 500px)'));
      expect(landscapeShort).toBeDefined();

      const darkScheme = symbols.find(s => s.signature?.includes('(prefers-color-scheme: dark)'));
      expect(darkScheme).toBeDefined();

      const reducedMotion = symbols.find(s => s.signature?.includes('(prefers-reduced-motion: reduce)'));
      expect(reducedMotion).toBeDefined();

      const hoverPointer = symbols.find(s => s.signature?.includes('(hover: hover) and (pointer: fine)'));
      expect(hoverPointer).toBeDefined();

      // Print styles
      const printMedia = symbols.find(s => s.signature?.includes('@media print'));
      expect(printMedia).toBeDefined();

      // Font face
      const fontFace1 = symbols.find(s => s.signature?.includes('@font-face') && s.signature?.includes('font-weight: 400'));
      expect(fontFace1).toBeDefined();

      const fontFace2 = symbols.find(s => s.signature?.includes('@font-face') && s.signature?.includes('font-weight: 700'));
      expect(fontFace2).toBeDefined();

      // Animation classes
      const fadeInClass = symbols.find(s => s.name === '.fade-in');
      expect(fadeInClass).toBeDefined();
      expect(fadeInClass?.signature).toContain('animation: fadeIn 0.6s ease-out');

      const slideInClass = symbols.find(s => s.name === '.slide-in-left');
      expect(slideInClass).toBeDefined();

      const bounceInClass = symbols.find(s => s.name === '.bounce-in');
      expect(bounceInClass).toBeDefined();

      const pulseClass = symbols.find(s => s.name === '.pulse-animation');
      expect(pulseClass).toBeDefined();

      const complexAnimation = symbols.find(s => s.name === '.complex-animation');
      expect(complexAnimation).toBeDefined();
      expect(complexAnimation?.signature).toContain('fadeIn 0.5s ease-out');
      expect(complexAnimation?.signature).toContain('slideInFromLeft 0.8s 0.2s ease-out both');
    });
  });

  describe('Advanced CSS Selectors and Pseudo-elements', () => {
    it('should extract complex selectors, combinators, and pseudo-elements', async () => {
      const cssCode = `
/* Complex combinators */
.parent > .direct-child {
  color: red;
}

.ancestor .descendant {
  color: blue;
}

.sibling + .adjacent {
  margin-left: 1rem;
}

.element ~ .general-sibling {
  opacity: 0.8;
}

/* Advanced pseudo-classes */
.item:first-child {
  margin-top: 0;
}

.item:last-child {
  margin-bottom: 0;
}

.item:nth-child(odd) {
  background-color: #f8f9fa;
}

.item:nth-child(even) {
  background-color: #ffffff;
}

.item:nth-child(3n + 1) {
  border-left: 3px solid #3498db;
}

.item:nth-last-child(2) {
  margin-bottom: 2rem;
}

.form-group:has(input:invalid) {
  border-color: #e74c3c;
}

.card:has(> .featured) {
  border: 2px solid gnew;
}

.container:not(.disabled):not(.loading) {
  opacity: 1;
}

.input:is(.text, .email, .password) {
  border: 1px solid #ddd;
}

.button:where(.primary, .secondary) {
  padding: 0.75rem 1rem;
}

/* Pseudo-elements */
.element::before {
  content: '';
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 2px;
  background-color: #3498db;
}

.element::after {
  content: attr(data-label);
  position: absolute;
  bottom: 100%;
  left: 50%;
  transform: translateX(-50%);
  background-color: #2c3e50;
  color: white;
  padding: 0.5rem;
  border-radius: 4px;
  font-size: 0.875rem;
  white-space: nowrap;
}

.quote::before {
  content: '"';
  font-size: 2rem;
  color: #3498db;
  line-height: 1;
}

.quote::after {
  content: '"';
  font-size: 2rem;
  color: #3498db;
  line-height: 1;
}

.list-item::marker {
  color: #e74c3c;
  font-weight: bnew;
}

.input::placehnewer {
  color: #95a5a6;
  font-style: italic;
}

.selection::selection {
  background-color: #3498db;
  color: white;
}

.text::first-line {
  font-weight: bnew;
  text-transform: uppercase;
}

.paragraph::first-letter {
  font-size: 3rem;
  float: left;
  line-height: 1;
  margin-right: 0.5rem;
  margin-top: 0.25rem;
}

/* Form pseudo-classes */
input:focus {
  outline: none;
  border-color: #3498db;
  box-shadow: 0 0 0 3px rgba(52, 152, 219, 0.2);
}

input:valid {
  border-color: #27ae60;
}

input:invalid {
  border-color: #e74c3c;
}

input:required {
  background-image: url('data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg" width="8" height="8"><circle cx="4" cy="4" r="2" fill="%23e74c3c"/></svg>');
  background-position: right 0.5rem center;
  background-repeat: no-repeat;
}

input:optional {
  background-image: none;
}

input:checked + label {
  font-weight: bnew;
  color: #27ae60;
}

input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

input:read-only {
  background-color: #f8f9fa;
}

/* Link pseudo-classes */
a:link {
  color: #3498db;
}

a:visited {
  color: #8e44ad;
}

a:hover {
  color: #2980b9;
  text-decoration: underline;
}

a:active {
  color: #e74c3c;
}

a:focus {
  outline: 2px solid #3498db;
  outline-offset: 2px;
}

/* Target pseudo-class */
.section:target {
  background-color: #fff3cd;
  border-left: 4px solid #ffc107;
  padding-left: 1rem;
}

/* Structural pseudo-classes */
.grid-item:nth-of-type(3n) {
  grid-column: span 2;
}

.heading:only-child {
  margin: 0;
}

.list:empty::before {
  content: 'No items to display';
  color: #6c757d;
  font-style: italic;
}

/* Language and direction pseudo-classes */
.content:lang(en) {
  quotes: '"' '"' "'" "'";
}

.content:lang(fr) {
  quotes: '«' '»' '"' '"';
}

.text:dir(rtl) {
  text-align: right;
}

.text:dir(ltr) {
  text-align: left;
}

/* Complex nested selectors */
.header .navigation ul li a:hover::after {
  content: '';
  position: absolute;
  bottom: -5px;
  left: 0;
  width: 100%;
  height: 2px;
  background-color: currentColor;
}

.card:has(.image) .content:not(.minimal) h2:first-of-type::before {
  content: '🖼 ';
  margin-right: 0.5rem;
}

/* Media query with complex selectors */
@media (max-width: 767px) {
  .mobile-nav:target ~ .overlay {
    display: block;
  }

  .menu-toggle:checked + .menu {
    transform: translateX(0);
  }

  .responsive-table th:nth-child(n+3) {
    display: none;
  }
}

/* Custom pseudo-classes (WebKit specific) */
input::-webkit-outer-spin-button,
input::-webkit-inner-spin-button {
  -webkit-appearance: none;
  margin: 0;
}

input[type="search"]::-webkit-search-cancel-button {
  -webkit-appearance: none;
  height: 1em;
  width: 1em;
  background: url('data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>') center/contain no-repeat;
}

::-webkit-scrollbar {
  width: 8px;
}

::-webkit-scrollbar-track {
  background: #f1f1f1;
  border-radius: 4px;
}

::-webkit-scrollbar-thumb {
  background: #c1c1c1;
  border-radius: 4px;
}

::-webkit-scrollbar-thumb:hover {
  background: #a8a8a8;
}
`;

      const result = await parserManager.parseFile('selectors.css', cssCode);
      const extractor = new CSSExtractor('css', 'selectors.css', cssCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Combinator selectors
      const directChild = symbols.find(s => s.name === '.parent > .direct-child');
      expect(directChild).toBeDefined();
      expect(directChild?.signature).toContain('color: red');

      const descendant = symbols.find(s => s.name === '.ancestor .descendant');
      expect(descendant).toBeDefined();

      const adjacent = symbols.find(s => s.name === '.sibling + .adjacent');
      expect(adjacent).toBeDefined();

      const generalSibling = symbols.find(s => s.name === '.element ~ .general-sibling');
      expect(generalSibling).toBeDefined();

      // Structural pseudo-classes
      const firstChild = symbols.find(s => s.name === '.item:first-child');
      expect(firstChild).toBeDefined();

      const lastChild = symbols.find(s => s.name === '.item:last-child');
      expect(lastChild).toBeDefined();

      const nthChildOdd = symbols.find(s => s.name === '.item:nth-child(odd)');
      expect(nthChildOdd).toBeDefined();

      const nthChildFormula = symbols.find(s => s.name === '.item:nth-child(3n + 1)');
      expect(nthChildFormula).toBeDefined();

      const nthLastChild = symbols.find(s => s.name === '.item:nth-last-child(2)');
      expect(nthLastChild).toBeDefined();

      // Modern pseudo-classes
      const hasInvalid = symbols.find(s => s.name === '.form-group:has(input:invalid)');
      expect(hasInvalid).toBeDefined();

      const hasFeatured = symbols.find(s => s.name === '.card:has(> .featured)');
      expect(hasFeatured).toBeDefined();

      const notDisabled = symbols.find(s => s.name === '.container:not(.disabled):not(.loading)');
      expect(notDisabled).toBeDefined();

      const isInputs = symbols.find(s => s.name === '.input:is(.text, .email, .password)');
      expect(isInputs).toBeDefined();

      const whereButtons = symbols.find(s => s.name === '.button:where(.primary, .secondary)');
      expect(whereButtons).toBeDefined();

      // Pseudo-elements
      const beforeElement = symbols.find(s => s.name === '.element::before');
      expect(beforeElement).toBeDefined();
      expect(beforeElement?.signature).toContain('content: \'\'');
      expect(beforeElement?.signature).toContain('position: absolute');

      const afterElement = symbols.find(s => s.name === '.element::after');
      expect(afterElement).toBeDefined();
      expect(afterElement?.signature).toContain('content: attr(data-label)');

      const quoteBefore = symbols.find(s => s.name === '.quote::before');
      expect(quoteBefore).toBeDefined();

      const quoteAfter = symbols.find(s => s.name === '.quote::after');
      expect(quoteAfter).toBeDefined();

      const marker = symbols.find(s => s.name === '.list-item::marker');
      expect(marker).toBeDefined();

      const placehnewer = symbols.find(s => s.name === '.input::placehnewer');
      expect(placehnewer).toBeDefined();

      const selection = symbols.find(s => s.name === '.selection::selection');
      expect(selection).toBeDefined();

      const firstLine = symbols.find(s => s.name === '.text::first-line');
      expect(firstLine).toBeDefined();

      const firstLetter = symbols.find(s => s.name === '.paragraph::first-letter');
      expect(firstLetter).toBeDefined();

      // Form pseudo-classes
      const inputFocus = symbols.find(s => s.name === 'input:focus');
      expect(inputFocus).toBeDefined();

      const inputValid = symbols.find(s => s.name === 'input:valid');
      expect(inputValid).toBeDefined();

      const inputInvalid = symbols.find(s => s.name === 'input:invalid');
      expect(inputInvalid).toBeDefined();

      const inputRequired = symbols.find(s => s.name === 'input:required');
      expect(inputRequired).toBeDefined();

      const inputChecked = symbols.find(s => s.name === 'input:checked + label');
      expect(inputChecked).toBeDefined();

      const inputDisabled = symbols.find(s => s.name === 'input:disabled');
      expect(inputDisabled).toBeDefined();

      // Link pseudo-classes
      const linkHover = symbols.find(s => s.name === 'a:hover');
      expect(linkHover).toBeDefined();

      const linkVisited = symbols.find(s => s.name === 'a:visited');
      expect(linkVisited).toBeDefined();

      const linkActive = symbols.find(s => s.name === 'a:active');
      expect(linkActive).toBeDefined();

      // Target pseudo-class
      const sectionTarget = symbols.find(s => s.name === '.section:target');
      expect(sectionTarget).toBeDefined();

      // Structural selectors
      const nthOfType = symbols.find(s => s.name === '.grid-item:nth-of-type(3n)');
      expect(nthOfType).toBeDefined();

      const onlyChild = symbols.find(s => s.name === '.heading:only-child');
      expect(onlyChild).toBeDefined();

      const emptyBefore = symbols.find(s => s.name === '.list:empty::before');
      expect(emptyBefore).toBeDefined();

      // Language pseudo-classes
      const langEn = symbols.find(s => s.name === '.content:lang(en)');
      expect(langEn).toBeDefined();

      const langFr = symbols.find(s => s.name === '.content:lang(fr)');
      expect(langFr).toBeDefined();

      // Direction pseudo-classes
      const dirRtl = symbols.find(s => s.name === '.text:dir(rtl)');
      expect(dirRtl).toBeDefined();

      const dirLtr = symbols.find(s => s.name === '.text:dir(ltr)');
      expect(dirLtr).toBeDefined();

      // Complex nested selectors
      const complexNested = symbols.find(s => s.name === '.header .navigation ul li a:hover::after');
      expect(complexNested).toBeDefined();

      const superComplex = symbols.find(s => s.name?.includes('.card:has(.image) .content:not(.minimal) h2:first-of-type::before'));
      expect(superComplex).toBeDefined();

      // WebKit pseudo-elements
      const webkitScrollbar = symbols.find(s => s.name === '::-webkit-scrollbar');
      expect(webkitScrollbar).toBeDefined();

      const webkitScrollbarThumb = symbols.find(s => s.name === '::-webkit-scrollbar-thumb');
      expect(webkitScrollbarThumb).toBeDefined();

      const webkitScrollbarThumbHover = symbols.find(s => s.name === '::-webkit-scrollbar-thumb:hover');
      expect(webkitScrollbarThumbHover).toBeDefined();
    });
  });
});