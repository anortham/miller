import { KotlinExtractor } from './src/extractors/kotlin-extractor.ts';
import { SwiftExtractor } from './src/extractors/swift-extractor.ts';
import { PHPExtractor } from './src/extractors/php-extractor.ts';
import { RazorExtractor } from './src/extractors/razor-extractor.ts';
import { CExtractor } from './src/extractors/c-extractor.ts';
import { CSSExtractor } from './src/extractors/css-extractor.ts';
import { HTMLExtractor } from './src/extractors/html-extractor.ts';
import { RegexExtractor } from './src/extractors/regex-extractor.ts';

console.log('✅ Testing extractor imports...');

try {
  console.log('KotlinExtractor:', !!KotlinExtractor);
  console.log('SwiftExtractor:', !!SwiftExtractor);
  console.log('PHPExtractor:', !!PHPExtractor);
  console.log('RazorExtractor:', !!RazorExtractor);
  console.log('CExtractor:', !!CExtractor);
  console.log('CSSExtractor:', !!CSSExtractor);
  console.log('HTMLExtractor:', !!HTMLExtractor);
  console.log('RegexExtractor:', !!RegexExtractor);
  console.log('✅ All imports successful!');
} catch (error) {
  console.error('❌ Import error:', error);
}