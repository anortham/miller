import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { SqlExtractor } from '../../extractors/sql-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('SqlExtractor', () => {
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

  describe('DDL - Data Definition Language', () => {
    it('should extract tables, columns, and constraints', async () => {
      const sqlCode = `
-- User management tables
CREATE TABLE users (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    username VARCHAR(50) UNIQUE NOT NULL,
    email VARCHAR(255) UNIQUE NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    first_name VARCHAR(100),
    last_name VARCHAR(100),
    date_of_birth DATE,
    is_active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,

    CONSTRAINT chk_email_format CHECK (email LIKE '%@%.%'),
    CONSTRAINT chk_age CHECK (date_of_birth < CURDATE()),
    INDEX idx_username (username),
    INDEX idx_email (email),
    INDEX idx_created_at (created_at)
);

CREATE TABLE user_profiles (
    user_id BIGINT,
    bio TEXT,
    avatar_url VARCHAR(500),
    social_links JSON,
    preferences JSON DEFAULT '{}',

    PRIMARY KEY (user_id),
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

-- Enum table for user roles
CREATE TABLE user_roles (
    id INT PRIMARY KEY,
    role_name ENUM('admin', 'moderator', 'user', 'guest') NOT NULL,
    permissions JSON
);

-- Complex table with various column types
CREATE TABLE analytics_events (
    id UUID DEFAULT gen_random_uuid() PRIMARY KEY,
    event_type VARCHAR(100) NOT NULL,
    user_id BIGINT,
    session_id VARCHAR(100),
    event_data JSONB,
    ip_address INET,
    user_agent TEXT,
    occurred_at TIMESTAMPTZ DEFAULT NOW(),

    FOREIGN KEY (user_id) REFERENCES users(id),
    PARTITION BY RANGE (occurred_at)
);`;

      const result = await parserManager.parseFile('test.sql', sqlCode);

      const extractor = new SqlExtractor('sql', 'test.sql', sqlCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract tables
      const usersTable = symbols.find(s => s.name === 'users' && s.kind === SymbolKind.Class);
      expect(usersTable).toBeDefined();
      expect(usersTable?.signature).toContain('CREATE TABLE users');

      const userProfilesTable = symbols.find(s => s.name === 'user_profiles');
      expect(userProfilesTable).toBeDefined();

      const userRolesTable = symbols.find(s => s.name === 'user_roles');
      expect(userRolesTable).toBeDefined();

      const analyticsTable = symbols.find(s => s.name === 'analytics_events');
      expect(analyticsTable).toBeDefined();

      // Should extract columns as fields
      const idColumn = symbols.find(s => s.name === 'id' && s.kind === SymbolKind.Field);
      expect(idColumn).toBeDefined();
      expect(idColumn?.signature).toContain('BIGINT PRIMARY KEY');

      const usernameColumn = symbols.find(s => s.name === 'username');
      expect(usernameColumn).toBeDefined();
      expect(usernameColumn?.signature).toContain('VARCHAR(50) UNIQUE NOT NULL');

      const emailColumn = symbols.find(s => s.name === 'email');
      expect(emailColumn).toBeDefined();

      const isActiveColumn = symbols.find(s => s.name === 'is_active');
      expect(isActiveColumn).toBeDefined();
      expect(isActiveColumn?.signature).toContain('BOOLEAN DEFAULT TRUE');

      // Should extract JSON columns
      const socialLinksColumn = symbols.find(s => s.name === 'social_links');
      expect(socialLinksColumn).toBeDefined();
      expect(socialLinksColumn?.signature).toContain('JSON');

      const eventDataColumn = symbols.find(s => s.name === 'event_data');
      expect(eventDataColumn).toBeDefined();
      expect(eventDataColumn?.signature).toContain('JSONB');

      // Should extract constraints
      const constraints = symbols.filter(s => s.kind === SymbolKind.Interface); // Using Interface for constraints
      expect(constraints.length).toBeGreaterThanOrEqual(2);

      // Should extract indexes
      const indexes = symbols.filter(s => s.signature?.includes('INDEX'));
      expect(indexes.length).toBeGreaterThanOrEqual(3);
    });
  });

  describe('DML - Data Manipulation Language', () => {
    it('should extract complex queries and CTEs', async () => {
      const sqlCode = `
-- Complex CTE query with window functions
WITH monthly_user_stats AS (
    SELECT
        DATE_TRUNC('month', created_at) as month,
        COUNT(*) as new_users,
        COUNT(*) OVER (ORDER BY DATE_TRUNC('month', created_at)) as cumulative_users
    FROM users
    WHERE created_at >= '2023-01-01'
    GROUP BY DATE_TRUNC('month', created_at)
),
active_user_metrics AS (
    SELECT
        user_id,
        COUNT(DISTINCT DATE(occurred_at)) as active_days,
        AVG(EXTRACT(EPOCH FROM (occurred_at - LAG(occurred_at) OVER (PARTITION BY user_id ORDER BY occurred_at)))) as avg_session_gap
    FROM analytics_events
    WHERE occurred_at >= NOW() - INTERVAL '30 days'
    GROUP BY user_id
    HAVING COUNT(DISTINCT DATE(occurred_at)) > 5
)
SELECT
    u.username,
    u.email,
    mus.month,
    mus.new_users,
    aum.active_days,
    aum.avg_session_gap,
    CASE
        WHEN aum.active_days > 20 THEN 'high_activity'
        WHEN aum.active_days > 10 THEN 'medium_activity'
        ELSE 'low_activity'
    END as activity_level,
    ROW_NUMBER() OVER (PARTITION BY mus.month ORDER BY aum.active_days DESC) as activity_rank
FROM users u
JOIN monthly_user_stats mus ON DATE_TRUNC('month', u.created_at) = mus.month
LEFT JOIN active_user_metrics aum ON u.id = aum.user_id
WHERE u.is_active = TRUE
ORDER BY mus.month DESC, aum.active_days DESC;

-- Recursive CTE for hierarchical data
WITH RECURSIVE user_hierarchy AS (
    -- Base case: top-level users
    SELECT id, username, manager_id, 0 as level, username as path
    FROM users
    WHERE manager_id IS NULL

    UNION ALL

    -- Recursive case: users with managers
    SELECT u.id, u.username, u.manager_id, uh.level + 1,
           uh.path || ' -> ' || u.username as path
    FROM users u
    JOIN user_hierarchy uh ON u.manager_id = uh.id
    WHERE uh.level < 10  -- Prevent infinite recursion
)
SELECT * FROM user_hierarchy ORDER BY level, path;

-- UPSERT operation (PostgreSQL syntax)
INSERT INTO user_profiles (user_id, bio, avatar_url)
VALUES (1, 'Software Engineer', 'https://example.com/avatar.jpg')
ON CONFLICT (user_id)
DO UPDATE SET
    bio = EXCLUDED.bio,
    avatar_url = EXCLUDED.avatar_url,
    updated_at = NOW();`;

      const result = await parserManager.parseFile('test.sql', sqlCode);

      const extractor = new SqlExtractor('sql', 'test.sql', sqlCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract CTEs as functions or views
      const monthlyStatsFunction = symbols.find(s => s.name === 'monthly_user_stats');
      expect(monthlyStatsFunction).toBeDefined();

      const activeUserMetrics = symbols.find(s => s.name === 'active_user_metrics');
      expect(activeUserMetrics).toBeDefined();

      const userHierarchy = symbols.find(s => s.name === 'user_hierarchy');
      expect(userHierarchy).toBeDefined();
      expect(userHierarchy?.signature).toContain('RECURSIVE');

      // Should extract main query columns/expressions as fields
      const activityLevel = symbols.find(s => s.name === 'activity_level');
      expect(activityLevel).toBeDefined();

      const activityRank = symbols.find(s => s.name === 'activity_rank');
      expect(activityRank).toBeDefined();

      // Should handle window functions
      const windowFunctions = symbols.filter(s => s.signature?.includes('OVER ('));
      expect(windowFunctions.length).toBeGreaterThan(0);
    });
  });

  describe('Stored Procedures and Functions', () => {
    it('should extract stored procedures, functions, and triggers', async () => {
      const sqlCode = `
-- Stored procedure with parameters
DELIMITER $$
CREATE PROCEDURE GetUserAnalytics(
    IN p_user_id BIGINT,
    IN p_start_date DATE,
    IN p_end_date DATE,
    OUT p_total_events INT,
    OUT p_unique_sessions INT
)
BEGIN
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    START TRANSACTION;

    SELECT
        COUNT(*) INTO p_total_events,
        COUNT(DISTINCT session_id) INTO p_unique_sessions
    FROM analytics_events
    WHERE user_id = p_user_id
      AND DATE(occurred_at) BETWEEN p_start_date AND p_end_date;

    COMMIT;
END$$
DELIMITER ;

-- User-defined function
CREATE FUNCTION CalculateUserScore(p_user_id BIGINT)
RETURNS DECIMAL(10,2)
READS SQL DATA
DETERMINISTIC
BEGIN
    DECLARE v_score DECIMAL(10,2) DEFAULT 0.0;
    DECLARE v_event_count INT;
    DECLARE v_account_age_days INT;

    SELECT COUNT(*), DATEDIFF(NOW(), created_at)
    INTO v_event_count, v_account_age_days
    FROM analytics_events ae
    JOIN users u ON ae.user_id = u.id
    WHERE ae.user_id = p_user_id;

    SET v_score = (v_event_count * 0.1) + (v_account_age_days * 0.01);

    RETURN COALESCE(v_score, 0.0);
END;

-- PostgreSQL function with JSON processing
CREATE OR REPLACE FUNCTION update_user_preferences(
    p_user_id BIGINT,
    p_preferences JSONB
) RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
    v_current_prefs JSONB;
    v_merged_prefs JSONB;
BEGIN
    -- Get current preferences
    SELECT preferences INTO v_current_prefs
    FROM user_profiles
    WHERE user_id = p_user_id;

    -- Merge with new preferences
    v_merged_prefs := COALESCE(v_current_prefs, '{}'::jsonb) || p_preferences;

    -- Update the user profile
    UPDATE user_profiles
    SET preferences = v_merged_prefs,
        updated_at = NOW()
    WHERE user_id = p_user_id;

    RETURN FOUND;
EXCEPTION
    WHEN OTHERS THEN
        RAISE LOG 'Error updating preferences for user %: %', p_user_id, SQLERRM;
        RETURN FALSE;
END;
$$;

-- Trigger for audit logging
CREATE TRIGGER audit_user_changes
    AFTER UPDATE ON users
    FOR EACH ROW
    WHEN (OLD.* IS DISTINCT FROM NEW.*)
BEGIN
    INSERT INTO audit_log (
        table_name,
        record_id,
        action,
        old_values,
        new_values,
        changed_by,
        changed_at
    ) VALUES (
        'users',
        NEW.id,
        'UPDATE',
        json_object(OLD.*),
        json_object(NEW.*),
        USER(),
        NOW()
    );
END;

-- View with complex aggregations
CREATE VIEW user_engagement_summary AS
SELECT
    u.id,
    u.username,
    u.email,
    COUNT(DISTINCT ae.session_id) as total_sessions,
    COUNT(ae.id) as total_events,
    MIN(ae.occurred_at) as first_event,
    MAX(ae.occurred_at) as last_event,
    AVG(EXTRACT(EPOCH FROM (ae.occurred_at - LAG(ae.occurred_at) OVER (PARTITION BY u.id ORDER BY ae.occurred_at)))) as avg_time_between_events,
    EXTRACT(DAYS FROM (MAX(ae.occurred_at) - MIN(ae.occurred_at))) as engagement_span_days,
    CalculateUserScore(u.id) as user_score
FROM users u
LEFT JOIN analytics_events ae ON u.id = ae.user_id
WHERE u.is_active = TRUE
GROUP BY u.id, u.username, u.email
HAVING COUNT(ae.id) > 0;`;

      const result = await parserManager.parseFile('test.sql', sqlCode);

      const extractor = new SqlExtractor('sql', 'test.sql', sqlCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract stored procedures
      const getUserAnalytics = symbols.find(s => s.name === 'GetUserAnalytics' && s.kind === SymbolKind.Function);
      expect(getUserAnalytics).toBeDefined();
      expect(getUserAnalytics?.signature).toContain('CREATE PROCEDURE');

      // Should extract function parameters
      const userIdParam = symbols.find(s => s.name === 'p_user_id');
      expect(userIdParam).toBeDefined();
      expect(userIdParam?.signature).toContain('IN p_user_id BIGINT');

      const totalEventsParam = symbols.find(s => s.name === 'p_total_events');
      expect(totalEventsParam).toBeDefined();
      expect(totalEventsParam?.signature).toContain('OUT');

      // Should extract user-defined functions
      const calculateUserScore = symbols.find(s => s.name === 'CalculateUserScore');
      expect(calculateUserScore).toBeDefined();
      expect(calculateUserScore?.signature).toContain('RETURNS DECIMAL(10,2)');

      const updateUserPrefs = symbols.find(s => s.name === 'update_user_preferences');
      expect(updateUserPrefs).toBeDefined();
      expect(updateUserPrefs?.signature).toContain('RETURNS BOOLEAN');
      expect(updateUserPrefs?.signature).toContain('LANGUAGE plpgsql');

      // Should extract variables
      const scoreVar = symbols.find(s => s.name === 'v_score');
      expect(scoreVar).toBeDefined();
      expect(scoreVar?.signature).toContain('DECLARE v_score DECIMAL(10,2)');

      const currentPrefsVar = symbols.find(s => s.name === 'v_current_prefs');
      expect(currentPrefsVar).toBeDefined();
      expect(currentPrefsVar?.signature).toContain('JSONB');

      // Should extract triggers
      const auditTrigger = symbols.find(s => s.name === 'audit_user_changes');
      expect(auditTrigger).toBeDefined();
      expect(auditTrigger?.signature).toContain('CREATE TRIGGER');
      expect(auditTrigger?.signature).toContain('AFTER UPDATE ON users');

      // Should extract views
      const engagementView = symbols.find(s => s.name === 'user_engagement_summary');
      expect(engagementView).toBeDefined();
      expect(engagementView?.signature).toContain('CREATE VIEW');

      // Should extract view columns
      const totalSessions = symbols.find(s => s.name === 'total_sessions');
      expect(totalSessions).toBeDefined();

      const userScore = symbols.find(s => s.name === 'user_score');
      expect(userScore).toBeDefined();
    });
  });

  describe('Database Schema and Indexes', () => {
    it('should extract indexes, constraints, and database objects', async () => {
      const sqlCode = `
-- Create schema
CREATE SCHEMA analytics;
CREATE SCHEMA user_management;

-- Unique indexes
CREATE UNIQUE INDEX idx_users_email_active
ON users (email)
WHERE is_active = TRUE;

-- Composite indexes
CREATE INDEX idx_events_user_time
ON analytics_events (user_id, occurred_at DESC)
INCLUDE (event_type, event_data);

-- Partial indexes
CREATE INDEX idx_recent_active_users
ON users (created_at, last_login_at)
WHERE is_active = TRUE
  AND last_login_at > NOW() - INTERVAL '30 days';

-- GIN index for JSON data
CREATE INDEX idx_event_data_gin
ON analytics_events
USING GIN (event_data jsonb_path_ops);

-- Full-text search index
CREATE INDEX idx_user_profiles_text_search
ON user_profiles
USING GIN (to_tsvector('english', bio));

-- Constraints
ALTER TABLE users
ADD CONSTRAINT chk_username_length
CHECK (LENGTH(username) >= 3 AND LENGTH(username) <= 50);

ALTER TABLE users
ADD CONSTRAINT chk_password_strength
CHECK (LENGTH(password_hash) >= 8);

-- Foreign key with custom actions
ALTER TABLE user_profiles
ADD CONSTRAINT fk_user_profiles_user_id
FOREIGN KEY (user_id) REFERENCES users(id)
ON DELETE CASCADE ON UPDATE RESTRICT;

-- Check constraint with complex logic
ALTER TABLE analytics_events
ADD CONSTRAINT chk_event_data_structure
CHECK (
    event_data IS NULL OR (
        jsonb_typeof(event_data) = 'object' AND
        event_data ? 'timestamp' AND
        jsonb_typeof(event_data->'timestamp') = 'string'
    )
);

-- Sequence
CREATE SEQUENCE user_id_seq
    START WITH 1000
    INCREMENT BY 1
    MINVALUE 1000
    MAXVALUE 9999999999
    CACHE 100;

-- Domain
CREATE DOMAIN email_address AS VARCHAR(255)
    CHECK (VALUE ~* '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$');

-- Enum type
CREATE TYPE user_status AS ENUM (
    'pending',
    'active',
    'suspended',
    'deleted'
);

-- Custom aggregate function
CREATE AGGREGATE mode(anyelement) (
    SFUNC = mode_state,
    STYPE = internal,
    FINALFUNC = mode_final
);`;

      const result = await parserManager.parseFile('test.sql', sqlCode);

      const extractor = new SqlExtractor('sql', 'test.sql', sqlCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Should extract schemas
      const analyticsSchema = symbols.find(s => s.name === 'analytics');
      expect(analyticsSchema).toBeDefined();
      expect(analyticsSchema?.signature).toContain('CREATE SCHEMA analytics');

      const userMgmtSchema = symbols.find(s => s.name === 'user_management');
      expect(userMgmtSchema).toBeDefined();

      // Should extract indexes
      const emailIndex = symbols.find(s => s.name === 'idx_users_email_active');
      expect(emailIndex).toBeDefined();
      expect(emailIndex?.signature).toContain('CREATE UNIQUE INDEX');
      expect(emailIndex?.signature).toContain('WHERE is_active = TRUE');

      const compositeIndex = symbols.find(s => s.name === 'idx_events_user_time');
      expect(compositeIndex).toBeDefined();
      expect(compositeIndex?.signature).toContain('(user_id, occurred_at DESC)');
      expect(compositeIndex?.signature).toContain('INCLUDE');

      const ginIndex = symbols.find(s => s.name === 'idx_event_data_gin');
      expect(ginIndex).toBeDefined();
      expect(ginIndex?.signature).toContain('USING GIN');

      const textSearchIndex = symbols.find(s => s.name === 'idx_user_profiles_text_search');
      expect(textSearchIndex).toBeDefined();
      expect(textSearchIndex?.signature).toContain('to_tsvector');

      // Should extract constraints
      const usernameConstraint = symbols.find(s => s.name === 'chk_username_length');
      expect(usernameConstraint).toBeDefined();
      expect(usernameConstraint?.signature).toContain('CHECK (LENGTH(username)');

      const passwordConstraint = symbols.find(s => s.name === 'chk_password_strength');
      expect(passwordConstraint).toBeDefined();

      const fkConstraint = symbols.find(s => s.name === 'fk_user_profiles_user_id');
      expect(fkConstraint).toBeDefined();
      expect(fkConstraint?.signature).toContain('FOREIGN KEY');
      expect(fkConstraint?.signature).toContain('ON DELETE CASCADE');

      const jsonConstraint = symbols.find(s => s.name === 'chk_event_data_structure');
      expect(jsonConstraint).toBeDefined();
      expect(jsonConstraint?.signature).toContain('jsonb_typeof');

      // Should extract sequences
      const userSequence = symbols.find(s => s.name === 'user_id_seq');
      expect(userSequence).toBeDefined();
      expect(userSequence?.signature).toContain('CREATE SEQUENCE');
      expect(userSequence?.signature).toContain('START WITH 1000');

      // Should extract domains
      const emailDomain = symbols.find(s => s.name === 'email_address');
      expect(emailDomain).toBeDefined();
      expect(emailDomain?.signature).toContain('CREATE DOMAIN');

      // Should extract custom types
      const userStatusEnum = symbols.find(s => s.name === 'user_status');
      expect(userStatusEnum).toBeDefined();
      expect(userStatusEnum?.signature).toContain('CREATE TYPE');
      expect(userStatusEnum?.signature).toContain('ENUM');

      // Should extract aggregate functions
      const modeAggregate = symbols.find(s => s.name === 'mode');
      expect(modeAggregate).toBeDefined();
      expect(modeAggregate?.signature).toContain('CREATE AGGREGATE');
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer SQL types and extract table relationships', async () => {
      const sqlCode = `
CREATE TABLE orders (
    id BIGINT PRIMARY KEY,
    user_id BIGINT NOT NULL,
    total_amount DECIMAL(10,2),
    status VARCHAR(20) DEFAULT 'pending',
    created_at TIMESTAMP DEFAULT NOW(),

    FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE order_items (
    id BIGINT PRIMARY KEY,
    order_id BIGINT NOT NULL,
    product_id BIGINT NOT NULL,
    quantity INT NOT NULL,
    unit_price DECIMAL(8,2) NOT NULL,

    FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE,
    FOREIGN KEY (product_id) REFERENCES products(id)
);

-- Join query for relationship testing
SELECT
    u.username,
    o.total_amount,
    oi.quantity,
    p.name as product_name
FROM users u
JOIN orders o ON u.id = o.user_id
JOIN order_items oi ON o.id = oi.order_id
JOIN products p ON oi.product_id = p.id
WHERE o.status = 'completed';`;

      const result = await parserManager.parseFile('test.sql', sqlCode);

      const extractor = new SqlExtractor('sql', 'test.sql', sqlCode);
      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);
      const types = extractor.inferTypes(symbols);

      // Should extract foreign key relationships
      expect(relationships.length).toBeGreaterThan(0);

      const orderUserRelation = relationships.find(r =>
        r.kind === 'references' &&
        r.metadata?.targetTable === 'users'
      );
      expect(orderUserRelation).toBeDefined();

      const orderItemsOrderRelation = relationships.find(r =>
        r.kind === 'references' &&
        r.metadata?.targetTable === 'orders'
      );
      expect(orderItemsOrderRelation).toBeDefined();

      // Should infer column types
      expect(types.size).toBeGreaterThan(0);

      const totalAmountColumn = symbols.find(s => s.name === 'total_amount');
      expect(totalAmountColumn).toBeDefined();
      if (totalAmountColumn) {
        const inferredType = types.get(totalAmountColumn.id);
        expect(inferredType).toContain('DECIMAL');
      }

      const statusColumn = symbols.find(s => s.name === 'status');
      expect(statusColumn).toBeDefined();
      if (statusColumn) {
        const inferredType = types.get(statusColumn.id);
        expect(inferredType).toContain('VARCHAR');
      }

      // Should extract join relationships from queries
      const joinRelations = relationships.filter(r => r.kind === 'joins');
      expect(joinRelations.length).toBeGreaterThanOrEqual(1);
    });
  });
});