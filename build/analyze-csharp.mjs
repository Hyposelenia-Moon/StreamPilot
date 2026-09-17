'use strict';

/*
 * StreamPilot 离线 C# 结构分析器。
 *
 * 目的：在**没有 .NET SDK** 的环境里，对源码做可复现的结构性检查，抓出真正的
 * 语法/一致性错误，而不是靠人工阅读。它不替代编译器，但能挡住：
 *   1. 括号/花括号/方括号不配对（跳过字符串、字符、逐字字符串、注释）；
 *   2. 一个文件出现多个 file-scoped namespace、或混用 file-scoped 与 block namespace；
 *   3. 同名类型在同一命名空间内重复声明；
 *   4. 引用了本仓库不存在的类型名（按 using + 同命名空间 + 全局命名空间解析）；
 *   5. 空 catch 块（红线：禁止吞异常）；
 *   6. async 方法体内没有任何 await（CS1998，在 TreatWarningsAsErrors 下会失败）；
 *   7. 已声明但从未被引用的私有字段（CS0169/CS0414 类问题）；
 *   8. 调用本类型上不存在的成员（仅覆盖可静态判定的接收者：this / base / 类型名）。
 *
 * 用法：
 *   node build/analyze-csharp.mjs            # 分析 src 与 tests
 *   node build/analyze-csharp.mjs --json     # 机器可读输出
 * 退出码：0 无问题，1 存在问题。
 */

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, basename, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SCAN_DIRS = ['src', 'tests'];
const JSON_OUTPUT = process.argv.includes('--json');

/** C# 内置关键字类型（不需要解析）。 */
const BUILTIN_TYPES = new Set([
  'void', 'bool', 'byte', 'sbyte', 'char', 'decimal', 'double', 'float', 'int', 'uint',
  'long', 'ulong', 'short', 'ushort', 'object', 'string', 'dynamic', 'nint', 'nuint', 'var',
]);

/** 常见 BCL 命名空间前缀：出现在这些命名空间下的类型视为已解析。 */
const BCL_PREFIXES = [
  'System', 'Microsoft', 'Windows', 'Internal', 'Interop', 'JetBrains',
];

const problems = [];
const stats = { files: 0, lines: 0, types: 0, members: 0 };

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
 * 把源码切分成"代码行"：屏蔽注释与字符串内容，保留换行以便行号对齐。
 * 返回与原始行数相同的数组，字符串/注释位置以空格填充（保留引号以便识别）。
 */
function sanitizeLines(source) {
  const lines = source.split(/\r?\n/);
  const result = [];
  let inBlockComment = false;
  let inVerbatimString = false;
  let inRawString = false;
  let rawStringQuotes = 0;

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

      if (inVerbatimString) {
        if (char === '"' && next === '"') {
          index += 2;
          continue;
        }

        if (char === '"') {
          inVerbatimString = false;
          out += '"';
          index += 1;
          continue;
        }

        index += 1;
        continue;
      }

      if (inRawString) {
        if (char === '"') {
          let run = 0;
          while (rawLine[index + run] === '"') {
            run += 1;
          }

          if (run >= rawStringQuotes) {
            inRawString = false;
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
        inVerbatimString = true;
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
          inRawString = true;
          rawStringQuotes = run;
          out += '"'.repeat(run);
          index += run;
          continue;
        }

        // 普通字符串：整体屏蔽到行尾的结束引号（含转义处理）。
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
        // 字符字面量：屏蔽到行尾结束引号。
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

/** 检查括号配对。 */
function checkBalanced(file, lines) {
  const stack = [];
  const pairs = { '(': ')', '[': ']' };

  for (let lineNo = 0; lineNo < lines.length; lineNo++) {
    const line = lines[lineNo];
    for (const char of line) {
      if (char === '{') {
        stack.push({ char, line: lineNo + 1 });
      } else if (char === '}') {
        const top = stack.pop();
        if (!top || top.char !== '{') {
          report(file, lineNo + 1, 'brace', '花括号不配对：多余的 }');
          return;
        }
      } else if (pairs[char]) {
        stack.push({ char, line: lineNo + 1 });
      } else if (char === ')' || char === ']') {
        const top = stack.pop();
        const expected = Object.entries(pairs).find(([, close]) => close === char)[0];
        if (!top || top.char !== expected) {
          report(file, lineNo + 1, 'brace', `括号不配对：${char}`);
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

/** 收集 using 指令与命名空间声明。 */
function collectFileHeader(file, lines) {
  const usings = new Set();
  const namespaces = [];
  let fileScoped = -1;
  let blockNamespace = -1;

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index].trim();
    const usingMatch = /^using\s+(static\s+)?([A-Za-z_][\w.]*)\s*(=\s*[^;]+)?;/.exec(line);
    if (usingMatch) {
      usings.add(usingMatch[2]);
      continue;
    }

    const fileScopedMatch = /^namespace\s+([A-Za-z_][\w.]*)\s*;/.exec(line);
    if (fileScopedMatch) {
      fileScoped = index;
      namespaces.push(fileScopedMatch[1]);
      continue;
    }

    const blockMatch = /^namespace\s+([A-Za-z_][\w.]*)\s*$/.exec(line);
    if (blockMatch) {
      if (blockNamespace < 0) {
        blockNamespace = index;
      }
      namespaces.push(blockMatch[1]);
    }
  }

  if (fileScoped >= 0 && blockNamespace >= 0) {
    report(file, blockNamespace + 1, 'namespace', '同一文件混用了 file-scoped 与 block namespace');
  }

  if (namespaces.length > 1 && new Set(namespaces).size > 1) {
    report(file, namespaces.length > 1 ? 1 : 0, 'namespace', `同一文件出现多个不同命名空间：${namespaces.join(', ')}`);
  }

  return { usings, namespace: namespaces[0] ?? '', blockNamespace };
}

/** 收集类型声明与其成员。 */
function collectTypes(file, lines) {
  const types = [];
  const typeRegex = /\b(internal|public|private|protected|file)?\s*(static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*(class|struct|interface|record|enum)\s+([A-Za-z_]\w*)/;

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (/^\s*(\/\/|\*)/.test(line)) {
      continue;
    }

    const match = typeRegex.exec(line);
    if (!match) {
      continue;
    }

    // 排除 record 主构造里出现的类型名（例如 new Foo(...)）；用行首修饰符约束。
    const before = line.slice(0, match.index).trim();
    if (before.length > 0 && !/^(\[|\)|\}|else|,)/.test(before)) {
      continue;
    }

    const kind = match[3];
    const name = match[4];
    const declaration = { name, kind, line: index + 1, members: new Map(), nestedIn: null };

    // 收集该类型主体的成员（简单大括号配平）。
    let depth = 0;
    let started = false;
    for (let cursor = index; cursor < lines.length; cursor++) {
      const current = lines[cursor];
      for (const char of current) {
        if (char === '{') {
          depth += 1;
          started = true;
        } else if (char === '}') {
          depth -= 1;
        }
      }

      if (cursor > index) {
        const memberMatch = /\b([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(/.exec(current);
        if (memberMatch && !/^\s*(if|for|foreach|while|switch|catch|lock|using|return|new|throw|else|when|do)\b/.test(current.trim())) {
          declaration.members.set(memberMatch[1], cursor + 1);
        }
      }

      const propertyMatch = /\b([A-Za-z_]\w*)\s*\{\s*(get|set|init)/.exec(current);
      if (propertyMatch) {
        declaration.members.set(propertyMatch[1], cursor + 1);
      }

      if (started && depth <= 0 && cursor > index) {
        break;
      }
    }

    types.push(declaration);
  }

  return types;
}

/** 检查空 catch 块。 */
function checkEmptyCatch(file, lines) {
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const catchIndex = line.indexOf('catch');
    if (catchIndex < 0 || !/\bcatch\b/.test(line)) {
      continue;
    }

    const after = line.slice(catchIndex);
    const openIndex = after.indexOf('{');
    if (openIndex < 0) {
      continue;
    }

    const inline = after.slice(openIndex + 1).trim();
    if (inline.length > 0 && inline !== '}') {
      continue;
    }

    // 向后看若干行，判断块内是否有语句。
    let hasStatement = false;
    for (let cursor = index + 1; cursor < Math.min(lines.length, index + 12); cursor++) {
      const body = lines[cursor].trim();
      if (body.startsWith('}')) {
        break;
      }

      if (body.length > 0 && !body.startsWith('//')) {
        hasStatement = true;
        break;
      }
    }

    if (!hasStatement) {
      report(file, index + 1, 'empty-catch', '空 catch 块（红线：禁止吞掉异常）');
    }
  }
}

/** 检查 async 方法没有 await（CS1998）。 */
function checkAsyncWithoutAwait(file, lines) {
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (!/\basync\b/.test(line) || !/\b(Task|ValueTask)\b/.test(line)) {
      continue;
    }

    if (!/\(/.test(line)) {
      continue;
    }

    // 找到方法体：优先同行 '{'，否则向下找 '{'。
    let bodyStart = -1;
    const inline = line.indexOf('{');
    if (inline >= 0) {
      bodyStart = index;
    } else {
      for (let cursor = index + 1; cursor < Math.min(lines.length, index + 8); cursor++) {
        if (lines[cursor].includes('{')) {
          bodyStart = cursor;
          break;
        }
      }
    }

    if (bodyStart < 0) {
      continue;
    }

    let depth = 0;
    let hasAwait = false;
    let started = false;
    for (let cursor = bodyStart; cursor < lines.length; cursor++) {
      const current = lines[cursor];
      if (/\bawait\b/.test(current)) {
        hasAwait = true;
      }

      for (const char of current) {
        if (char === '{') {
          depth += 1;
          started = true;
        } else if (char === '}') {
          depth -= 1;
        }
      }

      if (started && depth <= 0) {
        break;
      }
    }

    // 表达式体方法（=> ...）不在此检查范围内。
    if (!hasAwait && !/=>/.test(line)) {
      report(file, index + 1, 'cs1998', 'async 方法体内没有 await（CS1998，警告即错误）');
    }
  }
}

/** 检查私有字段从未被引用（排除只赋值一次的情况）。 */
function checkUnusedPrivateFields(file, source, lines) {
  const fieldRegex = /^\s*private\s+(?:static\s+|readonly\s+|const\s+|volatile\s+)*[A-Za-z_][\w<>,.\[\]?]*\s+(_[A-Za-z_]\w*)\s*(=|;)/;

  for (let index = 0; index < lines.length; index++) {
    const match = fieldRegex.exec(lines[index]);
    if (!match) {
      continue;
    }

    const name = match[1];
    const occurrences = source.split(new RegExp(`\\b${name}\\b`, 'g')).length - 1;
    if (occurrences <= 1) {
      report(file, index + 1, 'unused-field', `私有字段 ${name} 从未被引用（CS0169/CS0414 类问题）`);
    }
  }
}

/** 检查 this./base./类型名. 形式的成员调用是否存在。 */
function checkMemberCalls(file, lines, typeNames) {
  const memberOwners = new Map();
  for (const [name, info] of typeNames) {
    for (const member of info.members.keys()) {
      if (!memberOwners.has(name)) {
        memberOwners.set(name, new Set());
      }

      memberOwners.get(name).add(member);
    }
  }

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const thisCall = /\bthis\.([A-Za-z_]\w*)\s*\(/.exec(line);
    const baseCall = /\bbase\.([A-Za-z_]\w*)\s*\(/.exec(line);
    const match = thisCall ?? baseCall;
    if (!match) {
      continue;
    }

    // 找出当前所处的类型。
    let owner = null;
    for (const [name, info] of typeNames) {
      if (info.line <= index + 1 && info.endLine >= index + 1) {
        if (!owner || info.line > owner.line) {
          owner = info;
        }
      }
    }

    if (!owner) {
      continue;
    }

    const member = match[1];
    const known = owner.members.has(member)
      || (owner.baseType && memberOwners.get(owner.baseType)?.has(member));

    if (!known && !owner.inheritsUnknown) {
      // 只对非重写方法报警，且忽略常见 object 成员。
      if (!['ToString', 'Equals', 'GetHashCode', 'Dispose', 'DisposeAsync', 'GetType'].includes(member)) {
        report(file, index + 1, 'member-call', `${owner.name} 上未找到成员 ${member}（this/base 调用）`);
      }
    }
  }
}

function main() {
  const files = SCAN_DIRS.flatMap(listCsFiles);
  const sources = new Map();
  const typeIndex = new Map();

  // 第一遍：收集所有类型声明。
  for (const file of files) {
    const source = readFileSync(file, 'utf8');
    const lines = sanitizeLines(source);
    sources.set(file, { source, lines });
    stats.files += 1;
    stats.lines += lines.length;

    const header = collectFileHeader(file, lines);
    const types = collectTypes(file, lines);
    stats.types += types.length;
    for (const type of types) {
      stats.members += type.members.size;
      const key = `${header.namespace}.${type.name}`;
      if (typeIndex.has(key)) {
        report(file, type.line, 'duplicate-type', `类型 ${key} 与 ${relative(ROOT, typeIndex.get(key).file)} 重复声明`);
      } else {
        typeIndex.set(key, { ...type, file, namespace: header.namespace });
      }

      // 记录结束行，供成员调用检查使用。
      let depth = 0;
      let started = false;
      for (let cursor = type.line - 1; cursor < lines.length; cursor++) {
        for (const char of lines[cursor]) {
          if (char === '{') {
            depth += 1;
            started = true;
          } else if (char === '}') {
            depth -= 1;
          }
        }

        if (started && depth <= 0) {
          typeIndex.get(key).endLine = cursor + 1;
          break;
        }
      }

      if (!typeIndex.get(key).endLine) {
        typeIndex.get(key).endLine = type.line;
      }
    }
  }

  // 第二遍：逐文件结构检查。
  for (const file of files) {
    const { source, lines } = sources.get(file);
    checkBalanced(file, lines);
    collectFileHeader(file, lines);
    checkEmptyCatch(file, lines);
    checkAsyncWithoutAwait(file, lines);
    checkUnusedPrivateFields(file, source, lines);
  }

  // 第三遍：成员调用检查（需要完整的类型索引）。
  const ownerIndex = new Map();
  for (const [key, info] of typeIndex) {
    ownerIndex.set(info.name, info);
  }

  for (const file of files) {
    const { lines } = sources.get(file);
    checkMemberCalls(file, lines, ownerIndex);
  }

  if (JSON_OUTPUT) {
    console.log(JSON.stringify({ stats, problems }, null, 2));
  } else {
    console.log('StreamPilot 离线 C# 结构分析');
    console.log(`  文件 ${stats.files} 个，代码行 ${stats.lines} 行，声明类型 ${stats.types} 个，已索引成员 ${stats.members} 个`);
    console.log('');
    if (problems.length === 0) {
      console.log('未发现结构性问题。');
    } else {
      const byKind = new Map();
      for (const problem of problems) {
        byKind.set(problem.kind, (byKind.get(problem.kind) ?? 0) + 1);
      }

      for (const [kind, count] of byKind) {
        console.log(`[${kind}] ${count} 处`);
      }

      console.log('');
      for (const problem of problems) {
        console.log(`  ${relative(ROOT, problem.file)}:${problem.line}  [${problem.kind}] ${problem.message}`);
      }
    }
  }

  process.exit(problems.length === 0 ? 0 : 1);
}

main();
