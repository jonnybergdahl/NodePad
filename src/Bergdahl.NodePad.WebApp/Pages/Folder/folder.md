# Folder content

This is a page in the Folder directory.

# Lorem Ipsum Showcase — A Markdown Playground

> *A page that demonstrates common Markdown elements using classic lorem ipsum filler.*

---

## Table of Contents
- [Headings & Text Styles](#headings--text-styles)
- [Blockquotes](#blockquotes)
- [Lists](#lists)
- [Task List](#task-list)
- [Links & Images](#links--images)
- [Code & Syntax Highlighting](#code--syntax-highlighting)
- [Tables](#tables)
- [Horizontal Rules](#horizontal-rules)
- [Footnotes](#footnotes)
- [Extras](#extras)

---

## Headings & Text Styles

### Heading 3
#### Heading 4
##### Heading 5
###### Heading 6

**Bold** ipsum dolor sit amet, *italic* consectetur adipiscing elit, and ~~strikethrough~~ sed do eiusmod tempor.  
Inline code: `const dolor = "sit amet";` and combined **_bold italic_**.

Escapes: \*not bold\*, \_not italic\_, backslash: `\\`.

Superscript-ish: X^2^ (not standardized everywhere, shown literally).

---

## Blockquotes

> “Lorem ipsum dolor sit amet, consectetur adipiscing elit.”  
> — *Unknown*

> **Note:** Vivamus sagittis lacus vel augue laoreet rutrum faucibus dolor auctor.
>
> > Nested quote: Cras mattis consectetur purus sit amet fermentum.

---

## Lists

### Unordered
- Lorem ipsum dolor sit amet
- Consectetur adipiscing elit
    - Sub-item: Sed posuere consectetur est
    - Sub-item: Maecenas faucibus mollis interdum
- Integer posuere erat a ante

### Ordered
1. First: Aenean eu leo quam
2. Second: Pellentesque ornare sem lacinia
    1. Nested: Vestibulum id ligula porta
    2. Nested: Cras justo odio
3. Third: Donec ullamcorper nulla non metus

---

## Task List

- [x] Create a simple checklist
- [ ] Add more lorem to the list
- [ ] Celebrate with coffee ☕️

---

## Links & Images

Inline link: Visit [Example](https://example.com) for more ipsum.

Reference-style link: See the [reference link][docs] for extended dolor.

Image with alt text:

![Abstract placeholder image](https://picsum.photos/800/300)

*Caption:* *Praesent commodo cursus magna, vel scelerisque nisl consectetur et.*

---

## Code & Syntax Highlighting

Inline snippets like `printf("lorem\n");` are handy.

```bash
# Shell
echo "lorem ipsum" | tr '[:lower:]' '[:upper:]'
```

```javascript
// JavaScript
function lorem(words = 5) {
  const pool = ["lorem","ipsum","dolor","sit","amet"];
  return Array.from({ length: words }, (_, i) => pool[i % pool.length]).join(" ");
}
console.log(lorem(7));
```

```python
# Python
def dolor(n=3):
    items = ["lorem", "ipsum", "dolor", "sit", "amet"]
    return " ".join(items[:n])
print(dolor(4))
```

```json
{
  "title": "Lorem Ipsum",
  "items": ["lorem", "ipsum", "dolor"],
  "count": 3
}
```

> Tip: Use <kbd>Ctrl</kbd> + <kbd>F</kbd> (or <kbd>⌘</kbd> + <kbd>F</kbd> on macOS) to find “ipsum” occurrences.

---

## Tables

| Feature        | Description                                 | Notes                    |
|:---------------|:---------------------------------------------|:-------------------------|
| **Bold text**  | Emphasize important words                    | Use sparingly            |
| *Italic text*  | Subtle emphasis                              | Great for terms          |
| `Inline code`  | Short code or filenames                      | Backticks required       |
| Links          | `[text](url)`                                | Reference links optional |
| Images         | `![alt](url)`                                | Add captions below       |

---

## Horizontal Rules

Above and below this sentence are rules:

---

Interdum et malesuada fames ac ante ipsum primis in faucibus.

---

*Fin.*
