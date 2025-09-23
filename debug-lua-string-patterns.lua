
-- Basic string patterns
local function validateEmail(email)
  local pattern = "^[%w._-]+@[%w.-]+%.%w+$"
  return string.match(email, pattern) ~= nil
end

local function extractNumbers(text)
  local numbers = {}
  for number in string.gmatch(text, "%d+") do
    table.insert(numbers, tonumber(number))
  end
  return numbers
end

-- Advanced pattern matching
local function parseLogLine(line)
  local pattern = "(%d+%-%d+%-%d+) (%d+:%d+:%d+) %[(%w+)%] (.+)"
  local date, time, level, message = string.match(line, pattern)

  if date then
    return {
      date = date,
      time = time,
      level = level,
      message = message
    }
  else
    return nil
  end
end

local function extractUrls(text)
  local urls = {}
  local pattern = "https?://[%w%-._~:/?#%[%]@!$&'()*+,;=]+"

  for url in string.gmatch(text, pattern) do
    table.insert(urls, url)
  end

  return urls
end

-- String replacement and cleaning
local function cleanHtml(html)
  -- Remove HTML tags
  local cleaned = string.gsub(html, "<[^>]*>", "")

  -- Replace HTML entities
  local entities = {
    ["&lt;"] = "<",
    ["&gt;"] = ">",
    ["&amp;"] = "&",
    ["&quot;"] = '"',
    ["&apos;"] = "'",
    ["&#39;"] = "'"
  }

  for entity, replacement in pairs(entities) do
    cleaned = string.gsub(cleaned, entity, replacement)
  end

  -- Remove extra whitespace
  cleaned = string.gsub(cleaned, "%s+", " ")
  cleaned = string.gsub(cleaned, "^%s+", "")
  cleaned = string.gsub(cleaned, "%s+$", "")

  return cleaned
end

local function formatPhoneNumber(phone)
  -- Remove all non-digits
  local digits = string.gsub(phone, "%D", "")

  -- Format US phone numbers
  if #digits == 10 then
    local area = string.sub(digits, 1, 3)
    local exchange = string.sub(digits, 4, 6)
    local number = string.sub(digits, 7, 10)
    return "(" .. area .. ") " .. exchange .. "-" .. number
  elseif #digits == 11 and string.sub(digits, 1, 1) == "1" then
    local area = string.sub(digits, 2, 4)
    local exchange = string.sub(digits, 5, 7)
    local number = string.sub(digits, 8, 11)
    return "+1 (" .. area .. ") " .. exchange .. "-" .. number
  else
    return digits  -- Return cleaned digits if format unknown
  end
end

-- Template processing
local function processTemplate(template, variables)
  local result = template

  -- Replace {{variable}} patterns
  result = string.gsub(result, "{{(%w+)}}", function(varName)
    return tostring(variables[varName] or "")
  end)

  -- Replace {variable} patterns
  result = string.gsub(result, "{(%w+)}", function(varName)
    return tostring(variables[varName] or "")
  end)

  return result
end

local function parseQueryString(queryString)
  local params = {}

  -- Split by & and parse key=value pairs
  for pair in string.gmatch(queryString, "[^&]+") do
    local key, value = string.match(pair, "([^=]+)=([^=]*)")
    if key and value then
      -- URL decode
      key = string.gsub(key, "+", " ")
      key = string.gsub(key, "%%(%x%x)", function(hex)
        return string.char(tonumber(hex, 16))
      end)

      value = string.gsub(value, "+", " ")
      value = string.gsub(value, "%%(%x%x)", function(hex)
        return string.char(tonumber(hex, 16))
      end)

      params[key] = value
    end
  end

  return params
end

-- CSV parsing with patterns
local function parseCSV(csvText)
  local rows = {}
  local currentRow = {}
  local currentField = ""
  local inQuotes = false

  for i = 1, #csvText do
    local char = string.sub(csvText, i, i)

    if char == '"' then
      if inQuotes and i < #csvText and string.sub(csvText, i + 1, i + 1) == '"' then
        -- Escaped quote
        currentField = currentField .. '"'
        i = i + 1  -- Skip next quote
      else
        inQuotes = not inQuotes
      end
    elseif char == ',' and not inQuotes then
      table.insert(currentRow, currentField)
      currentField = ""
    elseif char == '\n' and not inQuotes then
      table.insert(currentRow, currentField)
      table.insert(rows, currentRow)
      currentRow = {}
      currentField = ""
    else
      currentField = currentField .. char
    end
  end

  -- Add last field and row if not empty
  if currentField ~= "" or #currentRow > 0 then
    table.insert(currentRow, currentField)
    table.insert(rows, currentRow)
  end

  return rows
end

-- Pattern-based validation
local Validator = {}
Validator.__index = Validator

function Validator:new()
  local instance = {
    patterns = {
      email = "^[%w._-]+@[%w.-]+%.%w+$",
      phone = "^%+?[%d%s%-()]+$",
      url = "^https?://[%w%-._~:/?#%[%]@!$&'()*+,;=]+$",
      creditCard = "^%d%d%d%d[%s%-]?%d%d%d%d[%s%-]?%d%d%d%d[%s%-]?%d%d%d%d$",
      zipCode = "^%d%d%d%d%d(%-?%d%d%d%d)?$",
      ipAddress = "^%d+%.%d+%.%d+%.%d+$"
    }
  }

  setmetatable(instance, Validator)
  return instance
end

function Validator:validate(value, type)
  local pattern = self.patterns[type]
  if not pattern then
    error("Unknown validation type: " .. type)
  end

  return string.match(value, pattern) ~= nil
end

function Validator:addPattern(name, pattern)
  self.patterns[name] = pattern
end

function Validator:getPattern(name)
  return self.patterns[name]
end

-- Text processing utilities
local TextUtils = {}

function TextUtils.splitLines(text)
  local lines = {}
  for line in string.gmatch(text, "[^\r\n]+") do
    table.insert(lines, line)
  end
  return lines
end

function TextUtils.splitWords(text)
  local words = {}
  for word in string.gmatch(text, "%S+") do
    table.insert(words, word)
  end
  return words
end

function TextUtils.capitalize(text)
  return string.gsub(text, "(%l)(%w*)", function(first, rest)
    return string.upper(first) .. rest
  end)
end

function TextUtils.camelCase(text)
  local result = string.gsub(text, "[-_](%l)", function(letter)
    return string.upper(letter)
  end)
  return string.gsub(result, "^%u", string.lower)
end

function TextUtils.snakeCase(text)
  local result = string.gsub(text, "(%u)", function(letter)
    return "_" .. string.lower(letter)
  end)
  return string.gsub(result, "^_", "")
end

function TextUtils.truncate(text, maxLength, suffix)
  suffix = suffix or "..."
  if #text <= maxLength then
    return text
  else
    return string.sub(text, 1, maxLength - #suffix) .. suffix
  end
end

-- Regular expression-like functionality
local function escapePattern(text)
  -- Escape Lua pattern special characters
  return string.gsub(text, "([%^%$%(%)%%%.%[%]%*%+%-%?])", "%%%1")
end

local function replaceAll(text, search, replacement)
  local escapedSearch = escapePattern(search)
  return string.gsub(text, escapedSearch, replacement)
end

local function contains(text, substring, ignoreCase)
  if ignoreCase then
    text = string.lower(text)
    substring = string.lower(substring)
  end

  return string.find(text, escapePattern(substring), 1, true) ~= nil
end
