-- Basic error handling with pcall
local function safeOperation(value)
  local success, result = pcall(function()
    if not value then
      error("Value is required")
    end

    if type(value) ~= "number" then
      error("Value must be a number, got " .. type(value))
    end

    return value * 2
  end)

  if success then
    return result
  else
    local errorMsg = "Operation failed: " .. result
    print(errorMsg)
    return nil
  end
end

-- xpcall with error handler
local function errorHandler(err)
  local traceback = debug.traceback(err, 2)
  local formatted = "Error occurred:\n" .. traceback
  print(formatted)
  return formatted
end

local function complexOperation(data)
  local success, result = xpcall(function()
    local processed = {}

    for i, item in ipairs(data) do
      if type(item) ~= "table" then
        error("Item " .. i .. " must be a table")
      end

      if not item.id then
        error("Item " .. i .. " missing required 'id' field")
      end

      local transformedItem = {
        id = item.id,
        value = item.value or 0,
        timestamp = os.time()
      }

      table.insert(processed, transformedItem)
    end

    return processed
  end, errorHandler)

  return success and result or nil
end

-- Assert-based validation
local function validateUser(user)
  assert(user, "User object is required")
  assert(type(user) == "table", "User must be a table")
  assert(user.name, "User name is required")
  assert(type(user.name) == "string", "User name must be a string")
  assert(#user.name > 0, "User name cannot be empty")

  if user.age then
    assert(type(user.age) == "number", "User age must be a number")
    assert(user.age >= 0, "User age must be non-negative")
    assert(user.age <= 150, "User age must be realistic")
  end

  if user.email then
    assert(type(user.email) == "string", "User email must be a string")
    assert(user.email:match("^[%w._-]+@[%w.-]+%.%w+$"), "Invalid email format")
  end

  return true
end

-- Custom error classes
local function createError(name, message, code)
  local error = {
    name = name,
    message = message,
    code = code,
    timestamp = os.time()
  }

  function error:toString()
    return self.name .. " (" .. (self.code or "unknown") .. "): " .. self.message
  end

  function error:getDetails()
    return {
      name = self.name,
      message = self.message,
      code = self.code,
      timestamp = self.timestamp
    }
  end

  return error
end

-- Validation error
local ValidationError = {}
ValidationError.__index = ValidationError

function ValidationError:new(message, field)
  local instance = createError("ValidationError", message, "VALIDATION_FAILED")
  instance.field = field
  setmetatable(instance, ValidationError)
  return instance
end

function ValidationError:getField()
  return self.field
end

-- Network error
local NetworkError = {}
NetworkError.__index = NetworkError

function NetworkError:new(message, statusCode, url)
  local instance = createError("NetworkError", message, statusCode)
  instance.url = url
  setmetatable(instance, NetworkError)
  return instance
end

function NetworkError:isRetryable()
  return self.code >= 500 or self.code == 429  -- Server errors or rate limit
end

-- Result/Maybe pattern
local Result = {}
Result.__index = Result

function Result:success(value)
  return setmetatable({
    isSuccess = true,
    value = value,
    error = nil
  }, Result)
end

function Result:failure(error)
  return setmetatable({
    isSuccess = false,
    value = nil,
    error = error
  }, Result)
end

function Result:map(fn)
  if self.isSuccess then
    local success, result = pcall(fn, self.value)
    if success then
      return Result:success(result)
    else
      return Result:failure(result)
    end
  else
    return self
  end
end

function Result:flatMap(fn)
  if self.isSuccess then
    local success, result = pcall(fn, self.value)
    if success and type(result) == "table" and result.isSuccess ~= nil then
      return result
    elseif success then
      return Result:success(result)
    else
      return Result:failure(result)
    end
  else
    return self
  end
end

function Result:getOrElse(defaultValue)
  return self.isSuccess and self.value or defaultValue
end

-- Safe division with Result pattern
local function safeDivide(a, b)
  if type(a) ~= "number" or type(b) ~= "number" then
    return Result:failure(ValidationError:new("Arguments must be numbers"))
  end

  if b == 0 then
    return Result:failure(ValidationError:new("Division by zero"))
  end

  return Result:success(a / b)
end

-- Retry mechanism
local function withRetry(operation, maxAttempts, delay)
  local attempts = 0
  local lastError = nil

  while attempts < maxAttempts do
    attempts = attempts + 1

    local success, result = pcall(operation)

    if success then
      return result
    else
      lastError = result

      local errorDetails = "Attempt " .. attempts .. " failed: " .. tostring(result)
      print(errorDetails)

      if attempts < maxAttempts then
        local waitTime = delay * attempts  -- Linear backoff
        -- In real code: sleep(waitTime)
      end
    end
  end

  error("Operation failed after " .. maxAttempts .. " attempts. Last error: " .. tostring(lastError))
end

-- Circuit breaker pattern
local function createCircuitBreaker(threshold, timeout)
  local breaker = {
    failureCount = 0,
    threshold = threshold,
    timeout = timeout,
    state = "closed",  -- closed, open, half-open
    lastFailureTime = 0
  }

  function breaker:call(operation)
    local currentTime = os.time()

    -- Check if circuit should move from open to half-open
    if self.state == "open" and currentTime - self.lastFailureTime > self.timeout then
      self.state = "half-open"
    end

    -- Reject calls when circuit is open
    if self.state == "open" then
      error("Circuit breaker is open")
    end

    local success, result = pcall(operation)

    if success then
      -- Reset on success
      self.failureCount = 0
      if self.state == "half-open" then
        self.state = "closed"
      end
      return result
    else
      -- Handle failure
      self.failureCount = self.failureCount + 1
      self.lastFailureTime = currentTime

      if self.failureCount >= self.threshold then
        self.state = "open"
      end

      error(result)
    end
  end

  return breaker
end