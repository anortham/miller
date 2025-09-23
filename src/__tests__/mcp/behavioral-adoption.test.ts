import { describe, test, expect } from 'bun:test';
import { readFileSync } from 'fs';
import path from 'path';

/**
 * Behavioral Adoption Test Suite
 *
 * Tests that Miller's MCP server instructions follow CodeSearch's proven
 * psychology-based approach for tool adoption by AI agents.
 *
 * Key principles being tested:
 * 1. Emotional positioning over technical features
 * 2. Workflow psychology that builds momentum
 * 3. Success celebration and confidence language
 * 4. Clear triggers for when to use tools
 */

describe('Behavioral Adoption Tests', () => {
  // Read actual MCP server instructions for testing
  const getCurrentInstructions = (): string => {
    const mcpServerPath = path.join(process.cwd(), 'src', 'mcp-server.ts');
    const content = readFileSync(mcpServerPath, 'utf-8');

    // Find the start of instructions and extract to the matching closing backtick
    const instructionsStart = content.indexOf('instructions: `');
    if (instructionsStart === -1) {
      throw new Error('Could not find instructions start in mcp-server.ts');
    }

    // Find the actual content start (after the opening backtick)
    const contentStart = instructionsStart + 'instructions: `'.length;

    // Find the closing backtick that's not escaped
    let contentEnd = contentStart;
    let bracketCount = 0;

    for (let i = contentStart; i < content.length; i++) {
      if (content[i] === '`' && content[i-1] !== '\\') {
        // Found unescaped backtick
        if (bracketCount === 0) {
          contentEnd = i;
          break;
        }
      }
    }

    if (contentEnd === contentStart) {
      throw new Error('Could not find instructions end in mcp-server.ts');
    }

    return content.substring(contentStart, contentEnd);
  };

  const getTargetInstructions = () => {
    // Target instructions following CodeSearch psychology
    return `# Welcome to Miller - Your Code Intelligence Companion! 🧠

## The Satisfaction of True Understanding

You now have access to Miller's revolutionary code intelligence that transforms
how you think about and work with code. This isn't just faster search - it's
the confidence that comes from truly understanding complex codebases.

## What Makes Development Deeply Satisfying

**The joy of architectural clarity:**
When exploring unfamiliar code, you get to:
1. **See the big picture** - \`explore("overview")\` reveals the heart of any codebase
2. **Follow the flow** - \`explore("trace")\` shows exactly how data moves through the system
3. **Connect the dots** - Cross-layer entity mapping links frontend → backend → database

This approach brings profound satisfaction - you're not guessing anymore,
you're working with complete knowledge.`;
  };

  describe('Emotional Positioning Tests', () => {
    test('should focus on outcomes rather than technical features', () => {
      const instructions = getCurrentInstructions();

      // Anti-patterns: technical feature focus
      const technicalPatterns = [
        /LIGHTNING-FAST/i,
        /100% ACCURATE/i,
        /SUPERPOWERS/i,
        /X-RAY VISION/i
      ];

      // Target patterns: outcome and emotional focus
      const emotionalPatterns = [
        /satisfaction/i,
        /confidence/i,
        /joy/i,
        /understanding/i,
        /clarity/i
      ];

      // Current instructions should trigger this test to fail
      // (showing we need to update them)
      let hasTechnicalFocus = false;
      let hasEmotionalFocus = false;

      technicalPatterns.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasTechnicalFocus = true;
        }
      });

      emotionalPatterns.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasEmotionalFocus = true;
        }
      });

      // This test should initially fail with current instructions
      expect(hasTechnicalFocus).toBe(false); // Should not have technical hype
      expect(hasEmotionalFocus).toBe(true);  // Should have emotional outcomes
    });

    test('should position development as craft rather than engineering', () => {
      const instructions = getCurrentInstructions();

      const craftLanguage = [
        /craft/i,
        /art/i,
        /elegant/i,
        /precise/i,
        /mastery/i
      ];

      const engineeringLanguage = [
        /engineering/i,
        /technical/i,
        /performance/i,
        /optimization/i
      ];

      let hasCraftLanguage = false;
      let hasEngineeringLanguage = false;

      craftLanguage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasCraftLanguage = true;
        }
      });

      engineeringLanguage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasEngineeringLanguage = true;
        }
      });

      // Should emphasize craft over pure engineering
      expect(hasCraftLanguage).toBe(true);
      expect(hasEngineeringLanguage).toBe(false);
    });
  });

  describe('Workflow Psychology Tests', () => {
    test('should provide step-by-step workflow that builds momentum', () => {
      const instructions = getCurrentInstructions();

      // Look for numbered sequences that create flow
      const workflowPatterns = [
        /1\.\s*\*\*.*\*\*/,  // 1. **Step name**
        /2\.\s*\*\*.*\*\*/,  // 2. **Step name**
        /3\.\s*\*\*.*\*\*/   // 3. **Step name**
      ];

      let hasWorkflowSequence = true;
      workflowPatterns.forEach(pattern => {
        if (!pattern.test(instructions)) {
          hasWorkflowSequence = false;
        }
      });

      expect(hasWorkflowSequence).toBe(true);
    });

    test('should use momentum-building language', () => {
      const instructions = getCurrentInstructions();

      const momentumWords = [
        /effortless/i,
        /flow state/i,
        /builds momentum/i,
        /sequence/i,
        /then/i,
        /next/i
      ];

      let hasMomentumLanguage = false;
      momentumWords.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasMomentumLanguage = true;
        }
      });

      expect(hasMomentumLanguage).toBe(true);
    });

    test('should position each tool in context of complete workflow', () => {
      const instructions = getCurrentInstructions();

      // Tools should be presented as part of a sequence
      const contextualToolUsage = [
        /explore.*then.*navigate/i,
        /search.*then.*edit/i,
        /understand.*then.*modify/i,
        /workflow/i
      ];

      let hasContextualUsage = false;
      contextualToolUsage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasContextualUsage = true;
        }
      });

      expect(hasContextualUsage).toBe(true);
    });
  });

  describe('Success Celebration Tests', () => {
    test('should celebrate moments of understanding and achievement', () => {
      const instructions = getCurrentInstructions();

      const celebrationLanguage = [
        /profound satisfaction/i,
        /deep rewards/i,
        /thrill/i,
        /success/i,
        /achievement/i,
        /clarity/i,
        /breakthrough/i
      ];

      let hasCelebrationLanguage = false;
      celebrationLanguage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasCelebrationLanguage = true;
        }
      });

      expect(hasCelebrationLanguage).toBe(true);
    });

    test('should frame debugging as detective work and refactoring as architectural clarity', () => {
      const instructions = getCurrentInstructions();

      const detectiveLanguage = [
        /detective/i,
        /investigate/i,
        /uncover/i,
        /reveal/i
      ];

      const architecturalLanguage = [
        /architectural clarity/i,
        /structural understanding/i,
        /design patterns/i,
        /system design/i
      ];

      let hasDetectiveFraming = false;
      let hasArchitecturalFraming = false;

      detectiveLanguage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasDetectiveFraming = true;
        }
      });

      architecturalLanguage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasArchitecturalFraming = true;
        }
      });

      expect(hasDetectiveFraming).toBe(true);
      expect(hasArchitecturalFraming).toBe(true);
    });
  });

  describe('Clear Trigger Tests', () => {
    test('should provide specific triggers for when to use each tool', () => {
      const instructions = getCurrentInstructions();

      const triggerPatterns = [
        /when.*use/i,
        /if.*then/i,
        /after.*tool/i,
        /before.*action/i
      ];

      let hasClearTriggers = false;
      triggerPatterns.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasClearTriggers = true;
        }
      });

      expect(hasClearTriggers).toBe(true);
    });

    test('should suggest next actions after each tool use', () => {
      const instructions = getCurrentInstructions();

      const nextActionPatterns = [
        /next.*step/i,
        /then.*use/i,
        /follow.*with/i,
        /continue.*by/i
      ];

      let hasNextActionGuidance = false;
      nextActionPatterns.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasNextActionGuidance = true;
        }
      });

      expect(hasNextActionGuidance).toBe(true);
    });
  });

  describe('Adoption Metrics Tests', () => {
    test('should measure reduction in hedging language', () => {
      // This would be tested through actual usage, but we can check
      // that instructions encourage confident language
      const instructions = getCurrentInstructions();

      const confidenceLanguage = [
        /know/i,
        /understand/i,
        /clear/i,
        /precise/i,
        /exact/i
      ];

      const hedgingLanguage = [
        /seems like/i,
        /appears to/i,
        /might be/i,
        /possibly/i,
        /maybe/i
      ];

      let hasConfidenceLanguage = false;
      let hasHedgingLanguage = false;

      confidenceLanguage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasConfidenceLanguage = true;
        }
      });

      hedgingLanguage.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasHedgingLanguage = true;
        }
      });

      expect(hasConfidenceLanguage).toBe(true);
      expect(hasHedgingLanguage).toBe(false);
    });

    test('should encourage sequential tool usage patterns', () => {
      const instructions = getCurrentInstructions();

      // Should encourage workflows like: explore → navigate → edit
      const sequentialPatterns = [
        /explore.*navigate/i,
        /search.*edit/i,
        /understand.*modify/i,
        /sequence/i,
        /workflow/i
      ];

      let hasSequentialGuidance = false;
      sequentialPatterns.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasSequentialGuidance = true;
        }
      });

      expect(hasSequentialGuidance).toBe(true);
    });
  });

  describe('Integration Tests', () => {
    test('should maintain technical accuracy while improving psychology', () => {
      const instructions = getCurrentInstructions();

      // Should still mention key technical capabilities
      const technicalCapabilities = [
        /20.*languages/i,
        /cross-language/i,
        /ast/i,
        /semantic/i,
        /tree-sitter/i
      ];

      let hasTechnicalAccuracy = false;
      technicalCapabilities.forEach(pattern => {
        if (pattern.test(instructions)) {
          hasTechnicalAccuracy = true;
        }
      });

      expect(hasTechnicalAccuracy).toBe(true);
    });

    test('should balance excitement with professionalism', () => {
      const instructions = getCurrentInstructions();

      // Should be exciting but not overwhelming
      const excitementLevel = (instructions.match(/!/g) || []).length;
      const capsByLine = instructions.split('\n')
        .filter(line => line.length > 0) // Filter out empty lines
        .map(line => (line.match(/[A-Z]/g) || []).length / line.length);

      const avgCapsRatio = capsByLine.length > 0 ?
        capsByLine.reduce((a, b) => a + b, 0) / capsByLine.length : 0;

      // Reasonable excitement levels
      expect(excitementLevel).toBeGreaterThan(5);   // Some excitement
      expect(excitementLevel).toBeLessThan(20);     // Not overwhelming
      expect(avgCapsRatio).toBeLessThan(0.3);       // Not too shouty
    });
  });
});