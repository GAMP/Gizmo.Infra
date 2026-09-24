namespace Gizmo.Infra.Tests.TestSupport;

/// <summary>
/// A Node-based stand-in for the <c>jq</c> interpreter. GitHub-hosted runners
/// ship real jq, but local runners may not, so executable tests inject this shim
/// while still running the action's own committed filter text and bash block.
/// It implements only the jq subset those blocks use, which keeps unsupported
/// filter changes failing loudly instead of silently passing.
/// </summary>
public static class JqShim
{
    /// <summary>
    /// Builds a bash function that shadows any <c>jq</c> on PATH with the shim,
    /// so the extracted action block calls the interpreter under test.
    /// </summary>
    public static string BashFunction(string scriptPath) =>
        $"jq() {{ node '{scriptPath.Replace('\\', '/')}' \"$@\"; }}";

    public const string JavaScript = """
        'use strict';

        const fs = require('fs');

        let raw = false;
        let exitStatus = false;
        const positional = [];
        for (const argument of process.argv.slice(2)) {
          if (/^-[a-zA-Z]+$/.test(argument)) {
            if (argument.includes('r')) raw = true;
            if (argument.includes('e')) exitStatus = true;
          } else {
            positional.push(argument);
          }
        }

        const program = positional[0] || '.';
        const file = positional[1];
        const input = file && file !== '-' ? fs.readFileSync(file, 'utf8') : fs.readFileSync(0, 'utf8');

        function main() {
          let data;
          try {
            data = JSON.parse(input);
          } catch (error) {
            process.stderr.write('jq: parse error: ' + error.message + '\n');
            return 2;
          }

          let value;
          try {
            value = parse(tokenize(program))(data);
          } catch (error) {
            process.stderr.write('jq: error: ' + error.message + '\n');
            return 5;
          }

          if (value === undefined) {
            return exitStatus ? 1 : 0;
          }

          const printed = raw && typeof value === 'string' ? value : JSON.stringify(value);
          process.stdout.write(printed + '\n');
          return exitStatus && (value === null || value === false) ? 1 : 0;
        }

        function tokenize(source) {
          const tokens = [];
          let index = 0;
          while (index < source.length) {
            const character = source[index];
            if (/\s/.test(character)) {
              index++;
              continue;
            }

            if (character === '"') {
              let cursor = index + 1;
              let text = '';
              while (cursor < source.length) {
                if (source[cursor] === '\\') {
                  text += source[cursor + 1];
                  cursor += 2;
                  continue;
                }
                if (source[cursor] === '"') break;
                text += source[cursor];
                cursor++;
              }
              if (source[cursor] !== '"') throw new Error('unterminated string');
              tokens.push({ type: 'string', value: text });
              index = cursor + 1;
              continue;
            }

            if (character === '.') {
              let cursor = index + 1;
              let name = '';
              while (cursor < source.length && /[A-Za-z0-9_]/.test(source[cursor])) {
                name += source[cursor];
                cursor++;
              }
              tokens.push({ type: 'field', value: name });
              index = cursor;
              continue;
            }

            if (source.startsWith('==', index) || source.startsWith('!=', index)) {
              tokens.push({ type: 'op', value: source.substr(index, 2) });
              index += 2;
              continue;
            }

            if ('|(),'.includes(character)) {
              tokens.push({ type: 'op', value: character });
              index++;
              continue;
            }

            const identifier = /^[A-Za-z_][A-Za-z0-9_]*/.exec(source.slice(index));
            if (identifier) {
              tokens.push({ type: 'ident', value: identifier[0] });
              index += identifier[0].length;
              continue;
            }

            const number = /^[0-9]+(?:\.[0-9]+)?/.exec(source.slice(index));
            if (number) {
              tokens.push({ type: 'number', value: Number(number[0]) });
              index += number[0].length;
              continue;
            }

            throw new Error('unexpected character ' + JSON.stringify(character));
          }
          return tokens;
        }

        function parse(tokens) {
          let position = 0;
          const peek = () => tokens[position];
          const advance = () => tokens[position++];
          const matchesOperator = (value) => {
            const token = peek();
            return token !== undefined && token.type === 'op' && token.value === value;
          };
          const matchesIdentifier = (value) => {
            const token = peek();
            return token !== undefined && token.type === 'ident' && token.value === value;
          };
          const expectOperator = (value) => {
            if (!matchesOperator(value)) throw new Error('expected ' + value);
            advance();
          };
          const expectIdentifier = (value) => {
            if (!matchesIdentifier(value)) throw new Error('expected ' + value);
            advance();
          };

          function expression() {
            return pipe();
          }

          function pipe() {
            let node = or();
            while (matchesOperator('|')) {
              advance();
              const left = node;
              const right = or();
              node = (input) => right(left(input));
            }
            return node;
          }

          function or() {
            let node = and();
            while (matchesIdentifier('or')) {
              advance();
              const left = node;
              const right = and();
              node = (input) => truthy(left(input)) || truthy(right(input));
            }
            return node;
          }

          function and() {
            let node = comparison();
            while (matchesIdentifier('and')) {
              advance();
              const left = node;
              const right = comparison();
              node = (input) => truthy(left(input)) && truthy(right(input));
            }
            return node;
          }

          function comparison() {
            let node = primary();
            while (matchesOperator('==') || matchesOperator('!=')) {
              const operator = advance().value;
              const left = node;
              const right = primary();
              node = operator === '=='
                ? (input) => left(input) === right(input)
                : (input) => left(input) !== right(input);
            }
            return node;
          }

          function primary() {
            const token = peek();
            if (token === undefined) throw new Error('unexpected end of filter');

            if (token.type === 'op' && token.value === '(') {
              advance();
              const inner = expression();
              expectOperator(')');
              return inner;
            }

            if (token.type === 'string') {
              advance();
              const value = token.value;
              return () => value;
            }

            if (token.type === 'number') {
              advance();
              const value = token.value;
              return () => value;
            }

            if (token.type === 'field') {
              advance();
              const name = token.value;
              return name === ''
                ? (input) => input
                : (input) => (input !== null && typeof input === 'object' && name in input ? input[name] : null);
            }

            if (token.type === 'ident') {
              if (token.value === 'if') return conditional();
              if (token.value === 'type') {
                advance();
                return (input) => jsonType(input);
              }
              if (token.value === 'error') {
                advance();
                expectOperator('(');
                const message = advance();
                if (message === undefined || message.type !== 'string') throw new Error('error() expects a string');
                expectOperator(')');
                return () => { throw new Error(message.value); };
              }
              if (token.value === 'true') { advance(); return () => true; }
              if (token.value === 'false') { advance(); return () => false; }
              if (token.value === 'null') { advance(); return () => null; }
              throw new Error('unsupported function ' + token.value);
            }

            throw new Error('unsupported token ' + JSON.stringify(token.value));
          }

          function conditional() {
            expectIdentifier('if');
            const branches = [];
            let condition = expression();
            expectIdentifier('then');
            let consequent = expression();
            branches.push([condition, consequent]);

            while (matchesIdentifier('elif')) {
              advance();
              condition = expression();
              expectIdentifier('then');
              consequent = expression();
              branches.push([condition, consequent]);
            }

            expectIdentifier('else');
            const alternate = expression();
            expectIdentifier('end');

            return (input) => {
              for (const [test, result] of branches) {
                if (truthy(test(input))) return result(input);
              }
              return alternate(input);
            };
          }

          const filter = expression();
          if (position !== tokens.length) throw new Error('unexpected trailing input');
          return filter;
        }

        function truthy(value) {
          return value !== false && value !== null && value !== undefined;
        }

        function jsonType(value) {
          if (value === null || value === undefined) return 'null';
          if (Array.isArray(value)) return 'array';
          return typeof value;
        }

        process.exitCode = main();
        """;
}
