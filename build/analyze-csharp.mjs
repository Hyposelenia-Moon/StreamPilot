'use strict';

/*
 * StreamPilot 离线 C# 结构分析器。
 *
 * 定位：在**没有 .NET SDK** 的环境里提供一层可复现的结构检查；有 SDK 时编译器是唯一权威，
 * 本工具作为快速前置检查（例如改动后立刻跑一次）。
 *
 * 检查项（全部是可确定的、不依赖语义的规则，避免误报）：
 *   1. 括号 / 花括号 / 方括号配对（正确跳过字符串、逐字字符串、原始字符串、字符与注释）；
 *   2. 一个文件出现多个不同命名空间，或混用 file-scoped 与 block namespace；
 *   3. 同一命名空间内类型名重复声明；
 *   4. 空 catch 块（CLAUDE.md 红线：禁止吞掉异常）；
 *   5. async 方法体内没有任何 await（CS1998；本仓库 TreatWarningsAsErrors 下即为错误）；
 *   6. 声明后从未被引用的私有字段（CS0169/CS0414 类问题）；
 *   7. 单个方法体行数超过 300 行（CLAUDE.md 硬性上限）；
 *   8. 文件内 XML 文档注释里 `--` 出现在注释中（csproj/MSBuild 会直接报 MSB4025）。
 *
 * 用法：
 *   node build/analyze-csharp.mjs            # 分析 src 与 tests
 *   node build/analyze-csharp.mjs --json     # 机器可读输出
 * 退出码：0 无问题，1 存在问题。
 */

import { readFileSync, readdirSync } from 'node:fs';
import { join, relative, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SCAN_DIRS = ['src', 'tests'];
const JSON_OUTPUT = process.argv.includes('--json');
const MAX_METHOD_LINES = 300;
const BRACE = String.fromCharCode(123);
const CLOSE_BRACE = String.fromCharCode(125);
const OPEN_PAREN = String.fromCharCode(40);
const CLOSE_PAREN = String.fromCharCode(41);
const OPEN_BRACKET = String.fromCharCode(91);
const CLOSE_BRACKET = String.fromCharCode(93);

const problems = [];
const stats = { files: 0, lines: 0, types: 0, methods: 0 };

function report(file, line, kind, message) {
  problems.push({ file, line, kind, message });
}

function listCsFiles(dir) {
  const result = [];
  const walk = (current) => {
    let entries;
    try {
      entries = readdirSync(current, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      if (entry.name === 'bin' || entry.name === 'obj' || entry.name === '.git') {
        continue;
      }

      const full = join(current, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else if (entry.name.endsWith('.cs')) {
        result.push(full);
      }
    }
  };

  walk(join(ROOT, dir));
  return result;
}

/**
 * 屏蔽注释与字符串内容，保留换行以对齐行号；引号本身保留，便于后续识别。
 * @param {string} source 源码文本
 * @returns {string[]} 与源码行数一致的"代码化"行数组
 */
function sanitizeLines(source) {
  const lines = source.split(/\r?\n/);
  const result = [];
  let inBlockComment = false;
  let inVerbatim = false;
  let inRaw = false;
  let rawQuotes = 0;

  for (const rawLine of lines) {
    let out = '';
    let index = 0;

    while (index < rawLine.length) {
      const char = rawLine[index];
      const next = rawLine[index + 1];

      if (inBlockComment) {
        if (char === '*' && next === '/') {
          inBlockComment = false;
          index += 2;
          continue;
        }

        index += 1;
        continue;
      }

      if (inVerbatim) {
        if (char === '"' && next === '"') {
          index += 2;
          continue;
        }

        if (char === '"') {
          inVerbatim = false;
          out += '"';
          index += 1;
          continue;
        }

        index += 1;
        continue;
      }

      if (inRaw) {
        if (char === '"') {
          let run = 0;
          while (rawLine[index + run] === '"') {
            run += 1;
          }

          if (run >= rawQuotes) {
            inRaw = false;
            out += '""';
            index += run;
            continue;
          }

          index += run;
          continue;
        }

        index += 1;
        continue;
      }

      if (char === '/' && next === '/') {
        break;
      }

      if (char === '/' && next === '*') {
        inBlockComment = true;
        index += 2;
        continue;
      }

      if (char === '@' && next === '"') {
        inVerbatim = true;
        out += '@"';
        index += 2;
        continue;
      }

      if (char === '$' && next === '"') {
        out += '$';
        index += 1;
        continue;
      }

      if (char === '"') {
        let run = 0;
        while (rawLine[index + run] === '"') {
          run += 1;
        }

        if (run >= 3) {
          inRaw = true;
          rawQuotes = run;
          out += '"'.repeat(run);
          index += run;
          continue;
        }

        out += '"';
        index += 1;
        while (index < rawLine.length) {
          const inner = rawLine[index];
          if (inner === '\\') {
            index += 2;
            continue;
          }

          if (inner === '"') {
            out += '"';
            index += 1;
            break;
          }

          index += 1;
        }

        continue;
      }

      if (char === "'") {
        index += 1;
        while (index < rawLine.length) {
          const inner = rawLine[index];
          if (inner === '\\') {
            index += 2;
            continue;
          }

          index += 1;
          if (inner === "'") {
            break;
          }
        }

        out += "'x'";
        continue;
      }

      out += char;
      index += 1;
    }

    result.push(out);
  }

  return result;
}

function checkBalanced(file, lines) {
  const stack = [];
  const closing = { [OPEN_PAREN]: CLOSE_PAREN, [OPEN_BRACKET]: CLOSE_BRACKET, [BRACE]: CLOSE_BRACE };
  const openingOf = { [CLOSE_PAREN]: OPEN_PAREN, [CLOSE_BRACKET]: OPEN_BRACKET, [CLOSE_BRACE]: BRACE };

  for (let lineNo = 0; lineNo < lines.length; lineNo++) {
    for (const char of lines[lineNo]) {
      if (closing[char]) {
        stack.push({ char, line: lineNo + 1 });
        continue;
      }

      if (openingOf[char]) {
        const top = stack.pop();
        if (!top || top.char !== openingOf[char]) {
          report(file, lineNo + 1, 'brace', `括号不配对：多余的 ${char}`);
          return;
        }
      }
    }
  }

  if (stack.length > 0) {
    const open = stack[stack.length - 1];
    report(file, open.line, 'brace', `括号未闭合：${open.char}`);
  }
}

function collectHeader(file, lines) {
  const namespaces = [];
  let fileScopedLine = -1;
  let blockLine = -1;

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index].trim();

    const fileScoped = /^namespace\s+([A-Za-z_][\w.]*)\s*;/.exec(line);
    if (fileScoped) {
      if (fileScopedLine < 0) {
        fileScopedLine = index + 1;
      }

      namespaces.push(fileScoped[1]);
      continue;
    }

    const block = /^namespace\s+([A-Za-z_][\w.]*)\s*$/.exec(line);
    if (block) {
      if (blockLine < 0) {
        blockLine = index + 1;
      }

      namespaces.push(block[1]);
    }
  }

  if (fileScopedLine > 0 && blockLine > 0) {
    report(file, blockLine, 'namespace', '同一文件混用了 file-scoped 与 block namespace');
  }

  if (new Set(namespaces).size > 1) {
    report(file, fileScopedLine > 0 ? fileScopedLine : blockLine, 'namespace', `同一文件出现多个不同命名空间：${[...new Set(namespaces)].join(', ')}`);
  }

  return { namespace: namespaces[0] ?? '' };
}

function collectTypes(file, lines) {
  const types = [];
  const typeRegex = new RegExp(
    `\\b(internal|public|private|protected|file)?\\s*(static\\s+|sealed\\s+|abstract\\s+|partial\\s+|readonly\\s+|ref\\s+)*(class|struct|interface|record|enum)\\s+([A-Za-z_]\\w*)`);

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const match = typeRegex.exec(line);
    if (!match) {
      continue;
    }

    const before = line.slice(0, match.index).trim();
    if (before.length > 0 && !/^(\[|\)|\}|else|,)/.test(before)) {
      continue;
    }

    const name = match[4];

    // 找类型体结束行（大括号配平）。
    let depth = 0;
    let started = false;
    let endLine = index + 1;
    for (let cursor = index; cursor < lines.length; cursor++) {
      for (const char of lines[cursor]) {
        if (char === BRACE) {
          depth += 1;
          started = true;
        } else if (char === CLOSE_BRACE) {
          depth -= 1;
        }
      }

      if (started && depth <= 0) {
        endLine = cursor + 1;
        break;
      }
    }

    types.push({ name, kind: match[3], line: index + 1, endLine });
  }

  return types;
}

function checkDuplicateTypes(file, types, header, index) {
  for (const type of types) {
    const key = `${header.namespace}.${type.name}`;
    if (index.has(key)) {
      report(file, type.line, 'duplicate-type', `类型 ${key} 与 ${relative(ROOT, index.get(key).file)} 重复声明`);
    } else {
      index.set(key, { file, line: type.line });
    }

    stats.types += 1;
  }
}

function checkEmptyCatch(file, lines) {
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (!/\bcatch\b/.test(line)) {
      continue;
    }

    const openIndex = line.indexOf(BRACE, line.indexOf('catch'));
    if (openIndex < 0) {
      continue;
    }

    const inline = line.slice(openIndex + 1).replace(new RegExp(`\\${CLOSE_BRACE}`, 'g'), '').trim();
    if (inline.length > 0) {
      continue;
    }

    let hasStatement = false;
    for (let cursor = index + 1; cursor < Math.min(lines.length, index + 12); cursor++) {
      const body = lines[cursor].trim();
      if (body.startsWith(CLOSE_BRACE)) {
        break;
      }

      if (body.length > 0) {
        hasStatement = true;
        break;
      }
    }

    if (!hasStatement) {
      report(file, index + 1, 'empty-catch', '空 catch 块（红线：禁止吞掉异常）');
    }
  }
}

function checkMethods(file, lines) {
  const declarationPattern = new RegExp(`^\\s*(public|private|protected|internal)[^;]*\\${OPEN_PAREN}`);

  for (let index = 0; index < lines.length; index++) {
    if (!declarationPattern.test(lines[index])) {
      continue;
    }

    // 找方法体起始（同行或紧随其后的 '{'）。
    let bodyStart = -1;
    if (lines[index].includes(BRACE)) {
      bodyStart = index;
    } else {
      for (let cursor = index + 1; cursor < Math.min(lines.length, index + 8); cursor++) {
        if (lines[cursor].includes(BRACE)) {
          bodyStart = cursor;
          break;
        }
      }
    }

    if (bodyStart < 0) {
      continue;
    }

    let depth = 0;
    let started = false;
    let endLine = bodyStart;
    let hasAwait = false;
    for (let cursor = bodyStart; cursor < lines.length; cursor++) {
      if (/\bawait\b/.test(lines[cursor])) {
        hasAwait = true;
      }

      for (const char of lines[cursor]) {
        if (char === BRACE) {
          depth += 1;
          started = true;
        } else if (char === CLOSE_BRACE) {
          depth -= 1;
        }
      }

      if (started && depth <= 0) {
        endLine = cursor;
        break;
      }
    }

    stats.methods += 1;
    const length = endLine - index + 1;
    if (length > MAX_METHOD_LINES) {
      report(file, index + 1, 'long-method', `方法约 ${length} 行，超过 ${MAX_METHOD_LINES} 行上限`);
    }

    const isAsyncDeclaration = /\basync\b/.test(lines[index]) || /\basync\b/.test(lines[bodyStart] ?? '');
    const isExpressionBodied = lines.slice(index, bodyStart + 1).some((text) => text.includes('=>'));
    if (isAsyncDeclaration && !hasAwait && !isExpressionBodied) {
      report(file, index + 1, 'cs1998', 'async 方法体内没有 await（CS1998：本仓库警告即错误）');
    }
  }
}

function checkUnusedPrivateFields(file, source, lines) {
  const fieldRegex = /^\s*private\s+(?:static\s+|readonly\s+|const\s+|volatile\s+)*[A-Za-z_][\w<>,.?]*(\[\])?\s+(_[A-Za-z_]\w*)\s*(=|;)/;

  for (let index = 0; index < lines.length; index++) {
    const match = fieldRegex.exec(lines[index]);
    if (!match) {
      continue;
    }

    const name = match[2];
    const occurrences = source.split(new RegExp(`\\b${name}\\b`, 'g')).length - 1;
    if (occurrences <= 1) {
      report(file, index + 1, 'unused-field', `私有字段 ${name} 从未被引用（CS0169/CS0414 类问题）`);
    }
  }
}

function checkXmlDocComments(file, lines) {
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (line.includes('--') && /^\s*\/\/\//.test(line)) {
      report(file, index + 1, 'xml-doc', 'XML 文档注释中出现 "--"（编译器会报 XML 格式错误）');
    }
  }
}

function main() {
  const files = SCAN_DIRS.flatMap(listCsFiles);
  const typeIndex = new Map();

  for (const file of files) {
    const source = readFileSync(file, 'utf8');
    const lines = sanitizeLines(source);
    stats.files += 1;
    stats.lines += lines.length;

    checkBalanced(file, lines);
    const header = collectHeader(file, lines);
    const types = collectTypes(file, lines);
    checkDuplicateTypes(file, types, header, typeIndex);
    checkEmptyCatch(file, lines);
    checkMethods(file, lines);
    checkUnusedPrivateFields(file, source, lines);
    checkXmlDocComments(file, lines);
  }

  if (JSON_OUTPUT) {
    console.log(JSON.stringify({ stats, problems }, null, 2));
  } else {
    console.log('StreamPilot 离线 C# 结构分析');
    console.log(`  文件 ${stats.files} 个 / 代码行 ${stats.lines} 行 / 声明类型 ${stats.types} 个 / 方法 ${stats.methods} 个`);
    console.log('');

    if (problems.length === 0) {
      console.log('未发现结构性问题。');
    } else {
      const byKind = new Map();
      for (const problem of problems) {
        byKind.set(problem.kind, (byKind.get(problem.kind) ?? 0) + 1);
      }

      console.log(`发现问题 ${problems.length} 处：${[...byKind].map(([kind, count]) => `${kind}×${count}`).join('，')}`);
      console.log('');
      for (const problem of problems) {
        console.log(`  ${relative(ROOT, problem.file)}:${problem.line}  [${problem.kind}] ${problem.message}`);
      }
    }
  }

  process.exit(problems.length === 0 ? 0 : 1);
}

main();
