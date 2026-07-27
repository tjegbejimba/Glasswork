//! Bounded port of `Glasswork.Core.Services.FrontmatterParser`: parses and
//! serializes exactly the fields/sections the shared vertical slice (#370)
//! needs, preserving the same on-disk V2 Vault contract (`---` YAML
//! frontmatter, `## Subtasks` with `### [ ] text` headings + `- key: value`
//! metadata lines, `## Notes`, `## Related`). Deliberately NOT a full port:
//! Related-section wiki-link hydration, Links (ADR 0009), and every legacy
//! frontmatter key are out of scope for this spike.

use crate::model::{GlassworkTask, SubTask};
use once_cell::sync::Lazy;
use regex::Regex;
use std::collections::HashMap;
use std::fmt;

#[derive(Debug)]
pub struct ParseError(pub String);

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.0)
    }
}
impl std::error::Error for ParseError {}

static FRONTMATTER_RE: Lazy<Regex> =
    Lazy::new(|| Regex::new(r"(?s)^---\s*\n(.*?)\n---\s*\n?(.*)").unwrap());
static SUBTASK_HEADING_RE: Lazy<Regex> =
    Lazy::new(|| Regex::new(r"^### \[([ xX])\] (.+?)\s*$").unwrap());
static METADATA_LINE_RE: Lazy<Regex> =
    Lazy::new(|| Regex::new(r"^- ([a-z_][a-z0-9_]*): (.*)$").unwrap());

/// Rust's `regex` crate has no lookahead, unlike .NET's, so section extraction
/// (`## Heading` up to the next top-level `## ` heading or end-of-body) is
/// done by manual line scanning instead of the C# parser's lookahead regex.
/// Returns (byte offset of the heading line in `body`, section body content).
fn find_section<'a>(body: &'a str, heading: &str) -> Option<(usize, &'a str)> {
    let mut offset = 0usize;
    let mut lines = body.split_inclusive('\n').peekable();
    while let Some(line) = lines.next() {
        let trimmed = line.trim_end_matches(['\n', '\r']);
        if trimmed == heading {
            let section_start = offset + line.len();
            let mut section_end = body.len();
            let mut scan_offset = section_start;
            for next_line in body[section_start..].split_inclusive('\n') {
                let next_trimmed = next_line.trim_end_matches(['\n', '\r']);
                if next_trimmed.starts_with("## ") {
                    section_end = scan_offset;
                    break;
                }
                scan_offset += next_line.len();
            }
            return Some((offset, &body[section_start..section_end]));
        }
        offset += line.len();
    }
    None
}

/// Recognized subtask metadata keys, canonical serialization order (mirrors
/// `FrontmatterParser.MetadataOrder`; "status" is first-class, not in this list).
const METADATA_ORDER: &[&str] = &["ado", "completed", "blocker", "due", "my_day"];

#[derive(serde::Deserialize, serde::Serialize, Default)]
struct TaskFrontmatter {
    id: Option<String>,
    title: Option<String>,
    status: Option<String>,
    priority: Option<String>,
    created: Option<String>,
    due: Option<String>,
    #[serde(rename = "ado_title")]
    ado_title: Option<String>,
    #[serde(rename = "blocked_reason")]
    blocked_reason: Option<String>,
    #[serde(rename = "blocked_at")]
    blocked_at: Option<String>,
    #[serde(rename = "blocked_from_status")]
    blocked_from_status: Option<String>,
}

pub fn parse(content: &str) -> Result<GlassworkTask, ParseError> {
    let caps = FRONTMATTER_RE
        .captures(content)
        .ok_or_else(|| ParseError("missing YAML frontmatter delimiters (---)".into()))?;

    let yaml = &caps[1];
    let body = caps[2].trim();

    let fm: TaskFrontmatter = serde_yaml::from_str(yaml)
        .map_err(|e| ParseError(format!("invalid YAML frontmatter: {e}")))?;

    let status = fm.status.unwrap_or_else(|| "todo".to_string());
    let (subtasks, description) = parse_subtasks(body);

    Ok(GlassworkTask {
        id: fm.id.unwrap_or_default(),
        title: fm.title.unwrap_or_default(),
        priority: fm.priority.unwrap_or_else(|| "medium".to_string()),
        created: fm.created,
        due: fm.due,
        ado_title: fm.ado_title,
        blocked_reason: if status == "blocked" { fm.blocked_reason } else { None },
        blocked_at: if status == "blocked" { fm.blocked_at } else { None },
        blocked_from_status: if status == "blocked" { fm.blocked_from_status } else { None },
        status,
        description,
        notes: parse_notes(body),
        subtasks,
    })
}

fn parse_subtasks(body: &str) -> (Vec<SubTask>, String) {
    let Some((section_start, section_content)) = find_section(body, "## Subtasks") else {
        return (Vec::new(), body.trim().to_string());
    };
    let section_content = section_content.to_string();

    let mut subtasks = Vec::new();
    let mut current: Option<SubTask> = None;
    let mut notes_buf = String::new();
    let mut in_metadata_block = false;

    let finalize = |current: &mut Option<SubTask>, notes_buf: &mut String, out: &mut Vec<SubTask>| {
        if let Some(mut sub) = current.take() {
            sub.notes = notes_buf.trim().to_string();
            out.push(sub);
        }
        notes_buf.clear();
    };

    for raw_line in section_content.split('\n') {
        let line = raw_line.trim_end_matches('\r');
        if let Some(heading) = SUBTASK_HEADING_RE.captures(line) {
            finalize(&mut current, &mut notes_buf, &mut subtasks);
            current = Some(SubTask {
                is_completed: heading[1].trim().eq_ignore_ascii_case("x"),
                text: heading[2].trim().to_string(),
                ..Default::default()
            });
            in_metadata_block = true;
            continue;
        }

        if current.is_none() {
            continue;
        }

        if in_metadata_block {
            if line.trim().is_empty() {
                in_metadata_block = false;
                continue;
            }
            if let Some(meta) = METADATA_LINE_RE.captures(line) {
                let key = meta[1].to_string();
                let value = meta[2].trim().to_string();
                let sub = current.as_mut().unwrap();
                if key == "status" {
                    sub.status = Some(value);
                } else {
                    sub.metadata.insert(key, value);
                }
                continue;
            }
            in_metadata_block = false;
            notes_buf.push_str(line);
            notes_buf.push('\n');
            continue;
        }

        notes_buf.push_str(line);
        notes_buf.push('\n');
    }
    finalize(&mut current, &mut notes_buf, &mut subtasks);

    let clean_body = body[..section_start].trim().to_string();
    (subtasks, clean_body)
}

fn parse_notes(body: &str) -> String {
    match find_section(body, "## Notes") {
        Some((_, content)) => content.replace("\r\n", "\n").trim().to_string(),
        None => String::new(),
    }
}

pub fn serialize(task: &GlassworkTask) -> String {
    let fm = TaskFrontmatter {
        id: Some(task.id.clone()),
        title: Some(task.title.clone()),
        status: Some(task.status.clone()),
        priority: Some(task.priority.clone()),
        created: task.created.clone(),
        due: task.due.clone(),
        ado_title: task.ado_title.clone(),
        blocked_reason: if task.status == "blocked" { task.blocked_reason.clone() } else { None },
        blocked_at: if task.status == "blocked" { task.blocked_at.clone() } else { None },
        blocked_from_status: if task.status == "blocked" { task.blocked_from_status.clone() } else { None },
    };
    let yaml = serde_yaml::to_string(&fm).unwrap_or_default();
    let yaml = yaml.trim_end();

    let mut out = String::new();
    out.push_str("---\n");
    out.push_str(yaml);
    out.push_str("\n---\n\n");

    if !task.description.trim().is_empty() {
        out.push_str(task.description.trim());
        out.push_str("\n\n");
    }

    out.push_str("## Subtasks\n\n");
    for sub in &task.subtasks {
        let check = if sub.is_completed { "x" } else { " " };
        out.push_str(&format!("### [{check}] {}\n", sub.text));
        if let Some(status) = &sub.status {
            out.push_str(&format!("- status: {status}\n"));
        }
        let mut emitted: HashMap<&str, ()> = HashMap::new();
        for key in METADATA_ORDER {
            if let Some(val) = sub.metadata.get(*key) {
                out.push_str(&format!("- {key}: {val}\n"));
                emitted.insert(key, ());
            }
        }
        let mut remaining: Vec<_> = sub
            .metadata
            .iter()
            .filter(|(k, _)| !emitted.contains_key(k.as_str()))
            .collect();
        remaining.sort_by_key(|(k, _)| k.to_string());
        for (k, v) in remaining {
            out.push_str(&format!("- {k}: {v}\n"));
        }
        if !sub.notes.trim().is_empty() {
            out.push('\n');
            out.push_str(sub.notes.trim());
            out.push('\n');
        }
        out.push('\n');
    }

    out.push_str("## Notes\n\n");
    if !task.notes.trim().is_empty() {
        out.push_str(task.notes.trim());
        out.push_str("\n\n");
    }

    out.push_str("## Related\n\n");

    out.trim_end().to_string() + "\n"
}
