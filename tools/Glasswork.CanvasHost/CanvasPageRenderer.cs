using System.Text.Json;

namespace Glasswork.CanvasHost;

internal static class CanvasPageRenderer
{
    public static string Render(object payload, JsonSerializerOptions jsonOptions, string nonce)
    {
        var json = JsonSerializer.Serialize(payload, jsonOptions).Replace("</", "<\\/", StringComparison.Ordinal);
        return Template
            .Replace("__PAYLOAD__", json, StringComparison.Ordinal)
            .Replace("__NONCE__", nonce, StringComparison.Ordinal);
    }

    private const string Template = """
<!doctype html>
<html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="icon" href="data:,">
<title>Glasswork Tasks</title>
<style>
:root{color-scheme:light dark}*{box-sizing:border-box}
body{margin:0;padding:0;background:var(--background-color-default,#fff);color:var(--text-color-default,#1f2328);font:14px/1.5 var(--font-sans,Segoe UI,sans-serif)}
#app{display:flex;flex-direction:column;min-height:100vh}
.body-row{display:flex;flex-direction:row;align-items:stretch;flex:1}
.rail{flex:0 0 280px;max-width:280px;border-right:1px solid var(--border-color-default,#d0d7de);overflow-y:auto;padding:12px}
.rail-header{align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px}
.rail-header h2{margin:0;font-size:15px}
.rail-list{list-style:none;margin:0;padding:0;flex-direction:column;gap:6px}
details[open] .rail-header,details[open] .rail-list{display:flex}
details:not([open]) .rail-header,details:not([open]) .rail-list{display:none}
.rail-row{display:flex;align-items:flex-start;gap:6px;border:1px solid var(--border-color-default,#d0d7de);border-radius:8px;padding:6px}
.rail-row.selected{border-color:var(--border-color-accent,#0969da);background:var(--background-color-subtle,rgba(9,105,218,.08))}
.rail-row.unavailable{border-style:dashed}
.rail-select{flex:1;text-align:left;background:none;border:0;padding:2px;cursor:pointer;color:inherit;font:inherit}
.rail-title{font-weight:600;display:block}
.rail-meta{display:flex;flex-wrap:wrap;gap:6px;margin-top:4px;font-size:12px;color:var(--text-color-muted,#656d76)}
.chip{border:1px solid var(--border-color-default,#d0d7de);border-radius:999px;padding:0 6px}
.chip.blocked{color:var(--true-color-red,#cf222e);border-color:var(--true-color-red,#cf222e)}
.chip.unavailable{color:var(--true-color-red,#cf222e);border-color:var(--true-color-red,#cf222e)}
.chip.stale{color:var(--true-color-orange,#9a6700);border-color:var(--true-color-orange,#9a6700)}
.retry-btn{border:1px solid var(--border-color-default,#d0d7de);border-radius:999px;padding:0 8px;background:none;cursor:pointer;font:inherit;color:inherit}
.rail-updated{margin:0 0 8px;font-size:12px;color:var(--text-color-muted,#656d76)}
.stale-banner{margin:0 0 12px;padding:10px 12px;border:1px solid var(--true-color-orange,#9a6700);border-radius:8px;background:var(--background-color-attention,#fff8c5);display:flex;align-items:center;justify-content:space-between;gap:12px}
.remove-btn{border:0;background:none;color:inherit;cursor:pointer;font-size:14px;line-height:1;padding:4px}
.remove-btn:hover{color:var(--true-color-red,#cf222e)}
.restore-banner{margin:12px;padding:10px 12px;border:1px solid var(--true-color-red,#cf222e);border-radius:8px;background:var(--background-color-subtle,rgba(207,34,46,.08))}
.restore-banner p{margin:4px 0 0}
.drift-banner{margin:12px;display:flex;align-items:center;justify-content:space-between;gap:12px;padding:10px 12px;border-radius:8px;background:var(--background-color-attention,#fff8c5);border:1px solid var(--border-color-attention,#d4a72c);color:var(--text-color-default,#1f2328)}
.drift-banner button{flex-shrink:0;border:0;background:none;color:inherit;cursor:pointer;text-decoration:underline;font:inherit}
.detail{flex:1;min-width:0;padding:24px;max-width:900px}
.card,details{border:1px solid var(--border-color-default,#d0d7de);border-radius:12px;padding:16px;margin-top:16px;background:var(--background-color-subtle,transparent)}
h1{margin:0 0 8px;font-size:26px}h2{margin:0 0 8px;font-size:18px}.muted{color:var(--text-color-muted,#656d76)}.error{border-color:var(--true-color-red,#cf222e)}
summary{cursor:pointer;font-weight:600}.artifact-meta{font-size:12px;font-weight:400;margin-left:8px}.artifact-body{margin-top:12px}.artifact-actions{display:flex;gap:8px;flex-wrap:wrap;margin:8px 0}
button{font:inherit;color:inherit;background:var(--button-default-background,#f6f8fa);border:1px solid var(--border-color-default,#d0d7de);border-radius:6px;padding:5px 10px;cursor:pointer}
.inline-link{border:0;padding:0;background:none;color:var(--link-color,#0969da);text-decoration:underline}.blocked-link,.unresolved-link{color:var(--text-color-muted,#656d76)}
pre{white-space:pre-wrap;overflow:auto;max-height:480px;background:var(--background-color-muted,#f6f8fa);padding:12px;border-radius:8px;font-family:var(--font-mono,Consolas,monospace)}
img{display:block;max-width:100%;max-height:540px;object-fit:contain}iframe{display:block;width:100%;height:480px;border:1px solid var(--border-color-default,#d0d7de);border-radius:8px;background:white}
blockquote,.callout{margin:8px 0;padding:8px 12px;border-left:4px solid var(--border-color-accent,#0969da);background:var(--background-color-muted,#f6f8fa)}
.table-scroll{overflow-x:auto}table{border-collapse:collapse}th,td{border:1px solid var(--border-color-default,#d0d7de);padding:6px 8px}.reason{padding:10px;border-radius:6px;background:var(--background-color-muted,#f6f8fa)}
.sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
.title-row{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.type-chip{border:1px solid var(--border-color-default,#d0d7de);border-radius:999px;padding:0 8px;font-size:12px;text-transform:uppercase;color:var(--text-color-muted,#656d76)}
.blocked-text,.blocker-text{color:var(--true-color-red,#cf222e);font-weight:600}
.readonly-indicator{color:var(--text-color-muted,#656d76);font-size:12px;border:1px solid var(--border-color-default,#d0d7de);border-radius:6px;padding:6px 10px;margin:8px 0 0}
.task-actions{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-top:8px}
.copy-status{font-size:12px;color:var(--text-color-muted,#656d76)}
.subtask-list{display:flex;flex-direction:column;gap:6px;margin-top:8px}
.subtask-row{display:flex;align-items:flex-start;gap:8px;border:1px solid var(--border-color-default,#d0d7de);border-radius:8px;padding:8px}
.subtask-row.done .subtask-text{text-decoration:line-through;opacity:.6}
.subtask-body{flex:1;min-width:0}
.subtask-chips{display:flex;flex-wrap:wrap;gap:6px;margin-top:4px}
.subtask-notes{margin:4px 0 0;font-size:12px;opacity:.75;white-space:pre-wrap}
.related-row,.child-row,.backlink-row{display:flex;align-items:center;gap:8px;width:100%;text-align:left;margin-top:6px;padding:8px 10px}
.link-row{display:flex;align-items:center;gap:8px;margin-top:6px}
.link-badge{border-radius:2px;padding:2px 6px;font-size:10px;font-weight:600;color:white}
.metadata{opacity:.6;font-size:12px;margin-top:16px;display:flex;flex-direction:column;gap:4px}
/* Wide layout: the rail is a permanently visible sidebar. Narrow layout below
   turns it into an explicit disclosure (an accessible drawer) driven by a
   native <summary> toggle: closed state hides the rail content (display:none,
   overriding any residual browser default), open state shows it as a flex
   column. Once past the breakpoint, content is forced visible with
   `!important` regardless of the `open` attribute so resizing from a
   collapsed narrow drawer up to desktop always shows the fixed rail. */
@media(max-width:719px){
  .body-row{flex-direction:column}
  .rail{flex:0 0 auto;max-width:none;border-right:0;border-bottom:1px solid var(--border-color-default,#d0d7de);overflow-y:visible;overflow-x:hidden}
  .rail-summary{display:flex;align-items:center;justify-content:space-between;list-style:none}
  .rail-summary::-webkit-details-marker{display:none}
  .detail{padding:12px}
}
@media(min-width:720px){
  .rail-summary{display:none}
  .rail-header,.rail-list{display:flex!important}
}
@media(max-width:560px){.detail{padding:12px}.card,details{padding:12px}h1{font-size:22px}iframe{height:360px}}
@media(prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}pre,.reason,blockquote,.callout{background:#161b22}button{background:#21262d;border-color:#30363d}.card,details,iframe,.rail-row{border-color:#30363d}}
@media(prefers-reduced-motion:reduce){*{transition:none!important;animation:none!important;scroll-behavior:auto!important}}
@media(forced-colors:active){.rail-row.selected{outline:2px solid Highlight}.rail-select:focus-visible,.remove-btn:focus-visible,.retry-btn:focus-visible,button:focus-visible{outline:2px solid Highlight}}
</style></head><body><main id="app"></main>
<script nonce="__NONCE__">
"use strict";
let data=__PAYLOAD__;
const app=document.querySelector("#app");
const query=new URLSearchParams(location.search);
const token=query.get("token")||"";
let activePreview=null;
let previewGeneration=0;
let pollHandle=null;
let railOpen=true;
const wideQuery=window.matchMedia("(min-width:720px)");
// Tracks the last "Updated" text actually announced to assistive tech, so the
// aria-live region (below) only takes on live semantics when the timestamp
// really changed — otherwise every 5s poll would insert a fresh live-region
// node with identical text and most screen readers re-announce it anyway.
let lastAnnouncedUpdate=null;

function element(tag,className,text){const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=String(text);return node}
function api(path,params={}){const url=new URL(path,location.origin);url.searchParams.set("token",token);for(const [key,value] of Object.entries(params))url.searchParams.set(key,value);return url}
async function readText(path,params){const response=await fetch(api(path,params),{cache:"no-store"});if(!response.ok){let message="Content is unavailable.";try{message=(await response.json()).message||message}catch{}throw new Error(message)}return response.text()}
async function post(path,payload){const response=await fetch(api(path),{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(payload||{})});let body=null;try{body=await response.json()}catch{}if(!response.ok){throw new Error((body&&body.message)||"Action failed.")}return body}
async function getJson(path){const response=await fetch(api(path),{cache:"no-store"});return response.json()}

function dueLabel(due){if(!due)return null;const date=new Date(due);return isNaN(date)?null:date.toLocaleDateString(undefined,{month:"short",day:"numeric"})}
function pad2(n){return String(n).padStart(2,"0")}
function isoDate(value){const d=new Date(value);return isNaN(d)?null:`${d.getFullYear()}-${pad2(d.getMonth()+1)}-${pad2(d.getDate())}`}
function isoDateTime(value){const d=new Date(value);return isNaN(d)?null:`${isoDate(value)} ${pad2(d.getHours())}:${pad2(d.getMinutes())}`}
async function copyToClipboard(text,label,statusEl){
  try{await navigator.clipboard.writeText(text);statusEl.textContent=label+" copied";}
  catch{statusEl.textContent="Couldn't copy "+label.toLowerCase();}
  setTimeout(()=>{statusEl.textContent=""},4000);
}

function renderRailRow(member){
  const li=element("li","rail-row"+(member.taskId===data.selectedTaskId?" selected":"")+(member.isUnavailable?" unavailable":""));
  li.setAttribute("role","option");
  li.setAttribute("aria-selected",member.taskId===data.selectedTaskId?"true":"false");
  const select=element("button","rail-select");
  select.type="button";
  // A distinct dataset key (not data-task-id) so this button does not also
  // match the delegated #app click handler's "[data-task-id]" selector below
  // (issue #563) -- that handler POSTs /api/tasks/load, which reorders the
  // rail, so a rail-select click would otherwise both select AND reorder.
  select.dataset.railTaskId=member.taskId;
  if(member.taskId===data.selectedTaskId)select.setAttribute("aria-current","true");
  select.append(element("span","rail-title",member.title||member.taskId));
  const meta=element("span","rail-meta");
  if(member.isUnavailable){
    meta.append(element("span","chip unavailable","Unavailable"));
  }else{
    meta.append(element("span","chip",member.statusLabel));
    if(member.priority)meta.append(element("span","chip","Priority: "+member.priority));
    const due=dueLabel(member.due);
    if(due)meta.append(element("span","chip","Due "+due));
    if(member.isBlocked)meta.append(element("span","chip blocked","Blocked"));
    if(member.isStale)meta.append(element("span","chip stale","Stale"));
  }
  select.append(meta);
  select.addEventListener("click",()=>selectTask(member.taskId));
  li.append(select);
  if(member.isStale){
    const retry=element("button","retry-btn","Retry");
    retry.type="button";
    retry.title=member.staleError||"Couldn't refresh this Task.";
    retry.addEventListener("click",event=>{event.stopPropagation();retryTask(member.taskId)});
    li.append(retry);
  }
  const remove=element("button","remove-btn","✕");
  remove.type="button";
  remove.setAttribute("aria-label","Remove "+(member.title||member.taskId)+" from canvas");
  remove.title="Remove from canvas";
  remove.addEventListener("click",()=>unloadTask(member.taskId));
  li.append(remove);
  return li;
}

function renderRail(){
  const wide=element("div","rail");
  const details=element("details");
  const summary=element("summary","rail-summary","Loaded Tasks ("+data.members.length+")");
  details.append(summary);
  const header=element("div","rail-header");
  header.append(element("h2",null,"Loaded Tasks"));
  const clear=element("button",null,"Clear all");
  clear.type="button";
  clear.disabled=data.members.length===0&&!data.restoreError;
  clear.addEventListener("click",clearAll);
  header.append(clear);
  details.append(header);
  if(data.lastUpdatedUtc){
    const updatedAt=isoDateTime(data.lastUpdatedUtc);
    if(updatedAt){
      const updated=element("p","rail-updated","Updated "+updatedAt);
      // Announces auto-refresh (issue #560) to assistive tech without the
      // noise of making the whole rail list a live region: only this small,
      // infrequently-changing timestamp is polite/atomic. Every render
      // recreates this node from scratch (issue #563), so live semantics are
      // only attached when the announced value actually changed — otherwise
      // an unrelated 5s poll would insert a "new" live-region node with
      // identical text and get re-announced anyway.
      if(updatedAt!==lastAnnouncedUpdate){
        updated.setAttribute("aria-live","polite");
        updated.setAttribute("aria-atomic","true");
        lastAnnouncedUpdate=updatedAt;
      }
      details.append(updated);
    }
  }
  const list=element("ul","rail-list");
  list.setAttribute("role","listbox");
  list.setAttribute("aria-label","Loaded Tasks");
  if(data.members.length===0){
    list.append(element("li","muted","No Tasks loaded yet."));
  }else{
    data.members.forEach(member=>list.append(renderRailRow(member)));
  }
  details.append(list);
  details.open=wideQuery.matches?true:railOpen;
  details.addEventListener("toggle",()=>{if(!wideQuery.matches)railOpen=details.open});
  wide.append(details);
  return wide;
}

function renderEmptyState(){
  const card=element("article","card");
  card.append(element("h1",null,"Glasswork Tasks"),element("p","muted","Ask an agent to load a Glasswork Task to get started."));
  return card;
}

function renderRestoreBanner(){
  if(!data.restoreError)return null;
  const banner=element("div","restore-banner");
  banner.setAttribute("role","alert");
  banner.append(element("strong",null,"Couldn't restore the Loaded Tasks from a previous session."));
  banner.append(element("p",null,data.restoreError.message||data.restoreError.code||"The saved state could not be read."));
  return banner;
}

function renderDriftBanner(){
  if(!data.driftDetected)return null;
  const banner=element("div","drift-banner");
  banner.setAttribute("role","status");
  banner.append(element("span",null,data.driftMessage||"A newer version of this canvas is available. Reopen this session to update."));
  const dismiss=element("button",null,"Dismiss");
  dismiss.type="button";
  dismiss.addEventListener("click",()=>banner.remove());
  banner.append(dismiss);
  return banner;
}

function renderErrorState(detail){
  const card=element("article","card error");
  card.append(element("h1",null,"Task unavailable"),element("p",null,detail.message));
  return card;
}

function renderReference(body,row,reason){
  body.replaceChildren();
  body.append(element("p","reason",reason||row.referenceReason||"Inline content is unavailable."));
  const actions=element("div","artifact-actions");
  if(row.showOpenInObsidian){
    const obsidian=element("button",null,"Open in Obsidian");
    obsidian.addEventListener("click",()=>artifactAction(row,"open_in_obsidian",body));
    actions.append(obsidian);
  }
  const primary=element("button",null,row.canLaunchExternally?"Open externally":"Show in folder");
  primary.addEventListener("click",()=>artifactAction(row,row.primaryAction,body));
  actions.append(primary);
  if(row.canLaunchExternally){const show=element("button",null,"Show in folder");show.addEventListener("click",()=>artifactAction(row,"show_in_folder",body));actions.append(show)}
  body.append(actions);
}
async function artifactAction(row,operation,body){
  try{await post("/api/artifact/action",{taskId:data.selectedTaskId,name:row.fileName,operation})}
  catch(error){body.prepend(element("p","error",error.message))}
}
function sourceParams(row){return{task_id:data.selectedTaskId,name:row.fileName}}
async function showHtmlSource(row,body){
  previewGeneration++;
  if(activePreview?.body===body)activePreview=null;
  body.dataset.mode="source";
  body.replaceChildren();
  const controls=htmlControls(row,body);
  const source=element("pre",null,"Loading source…");
  body.append(controls,source);
  try{source.textContent=await readText("/api/artifact/source",sourceParams(row))}
  catch(error){renderReference(body,row,error.message)}
}
function htmlControls(row,body){
  const controls=element("div","artifact-actions");
  const source=element("button",null,"Source");source.addEventListener("click",()=>showHtmlSource(row,body));
  const preview=element("button",null,"Preview");preview.addEventListener("click",()=>activatePreview(row,body));
  const open=element("button",null,"Open externally");open.addEventListener("click",()=>artifactAction(row,"open_externally",body));
  controls.append(source,preview,open);return controls
}
function sanitizePreview(raw){
  const doc=new DOMParser().parseFromString(raw,"text/html");
  doc.querySelectorAll("script,iframe,object,embed,link,base,audio,video,source").forEach(node=>node.remove());
  doc.querySelectorAll("meta[http-equiv]").forEach(node=>{if((node.getAttribute("http-equiv")||"").toLowerCase()==="refresh")node.remove()});
  doc.querySelectorAll("*").forEach(node=>{
    for(const attr of [...node.attributes]){
      const name=attr.name.toLowerCase(),value=attr.value.toLowerCase();
      if(name.startsWith("on")||["src","srcset","href","action","formaction","poster"].includes(name)||(name==="style"&&(value.includes("url(")||value.includes("@import")||value.includes("expression("))))node.removeAttribute(attr.name);
    }
  });
  doc.querySelectorAll("style").forEach(node=>{const css=node.textContent.toLowerCase();if(css.includes("url(")||css.includes("@import")||css.includes("expression("))node.remove()});
  const policy=doc.createElement("meta");policy.httpEquiv="Content-Security-Policy";policy.content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'; media-src 'none'; object-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'";
  doc.head.prepend(policy);
  return "<!doctype html>"+doc.documentElement.outerHTML;
}
function evictPreview(){
  if(!activePreview)return;
  const {row,body}=activePreview;activePreview=null;body.dataset.mode="evicted";body.replaceChildren(htmlControls(row,body),element("p","reason","Preview closed — another preview is active."));
  const reactivate=element("button",null,"Re-activate preview");reactivate.addEventListener("click",()=>activatePreview(row,body));body.append(reactivate);
}
async function activatePreview(row,body){
  const generation=++previewGeneration;
  try{
    const raw=await readText("/api/artifact/source",sourceParams(row));
    if(generation!==previewGeneration)return;
    if(activePreview?.body!==body)evictPreview();
    body.dataset.mode="preview";body.replaceChildren(htmlControls(row,body));
    const frame=element("iframe");frame.setAttribute("sandbox","");frame.setAttribute("title",row.title+" preview");frame.srcdoc=sanitizePreview(raw);body.append(frame);
    activePreview={row,body,frame};
  }catch(error){renderReference(body,row,error.message)}
}
function renderImage(row,body){
  body.dataset.mode="image";
  const controls=element("div","artifact-actions");
  if(row.isSvg){const source=element("button",null,"View source");source.addEventListener("click",async()=>{body.dataset.mode="source";const pre=element("pre",null,"Loading source…");body.replaceChildren(controls,pre);try{pre.textContent=await readText("/api/artifact/source",sourceParams(row))}catch(error){renderReference(body,row,error.message)}});controls.append(source)}
  const image=element("img");image.alt=row.title;image.src=api("/api/artifact/image",sourceParams(row));image.addEventListener("error",()=>renderReference(body,row,"Image could not be decoded."));
  body.append(controls,image);
}
function renderArtifact(row,taskId){
  const details=element("details");details.open=row.isExpanded;details.dataset.stateKey="artifact:"+taskId+":"+row.fileName;
  const summary=element("summary",null,row.title);summary.append(element("span","artifact-meta",`${row.kind} · ${row.sizeDisplay} · ${row.timeBadge}`));details.append(summary);
  const body=element("div","artifact-body");body.setAttribute("data-mode","source");details.append(body);
  if(row.showOpenInObsidian){const open=element("button",null,"Open in Obsidian");open.addEventListener("click",()=>artifactAction(row,"open_in_obsidian",body));body.append(open)}
  const load=()=>{
    if(body.dataset.loaded)return;body.dataset.loaded="true";
    if(row.isReference){renderReference(body,row);return}
    if(row.kind==="markdown"){body.dataset.mode="markdown";const rendered=element("div","markdown");rendered.innerHTML=row.renderedBody||"";body.append(rendered);return}
    if(row.kind==="text"){body.dataset.mode="text";body.append(element("pre",null,row.body||""));return}
    if(row.kind==="image"){renderImage(row,body);return}
    if(row.kind==="html"){showHtmlSource(row,body);return}
    renderReference(body,row);
  };
  if(details.open)load();details.addEventListener("toggle",()=>{if(details.open)load()});return details;
}
function renderSubtaskRow(s){
  const row=element("div","subtask-row"+(s.isEffectivelyDone?" done":""));
  row.append(element("span",null,s.isEffectivelyDone?"☑":"☐"));
  const body=element("div","subtask-body");
  body.append(element("span","subtask-text",s.text));
  const chips=element("div","subtask-chips");
  if(s.statusPillVisible)chips.append(element("span","chip",s.statusPillText));
  if(s.dueVisible)chips.append(element("span","chip",s.dueChipText));
  if(chips.childNodes.length)body.append(chips);
  if(s.blockerVisible)body.append(element("p","blocker-text",s.blockerText));
  if(s.hasNotes)body.append(element("p","subtask-notes",s.notes));
  row.append(body);
  return row;
}
function renderTask(p){
  const section=element("section");
  const header=element("div");
  const titleRow=element("div","title-row");
  titleRow.append(element("h1",null,p.title||p.taskId));
  if(p.showType)titleRow.append(element("span","type-chip",p.type));
  header.append(titleRow);
  const metaParts=[p.status.label,"Priority: "+(p.priority||"—")];
  const due=dueLabel(p.due);
  if(due)metaParts.push("Due "+due);
  metaParts.push(p.taskId);
  header.append(element("p","muted",metaParts.join(" · ")));
  if(p.blockedStatusText)header.append(element("p","blocked-text",p.blockedStatusText));
  if(p.showParent){
    const parentLine=element("p",null,"Parent: ");
    if(p.parentIsTask){const btn=element("button","inline-link",p.parent);btn.dataset.taskId=p.parent;parentLine.append(btn)}
    else parentLine.append(document.createTextNode(p.parent));
    header.append(parentLine);
  }
  if(p.showAdoLink){
    const adoLine=element("p",null,`ADO: #${p.adoLink} — ${p.adoTitle||"linked"}`);
    if(p.adoUrl){const btn=element("button","inline-link","Open in ADO");btn.dataset.externalUrl=p.adoUrl;adoLine.append(document.createTextNode(" "),btn)}
    header.append(adoLine);
  }
  const actions=element("div","task-actions");
  const refresh=element("button",null,"Refresh");refresh.type="button";refresh.addEventListener("click",refreshSelected);
  const openGlasswork=element("button",null,"Open in Glasswork");openGlasswork.type="button";
  openGlasswork.addEventListener("click",()=>post("/api/link/action",{url:p.taskDeepLink}).catch(()=>{}));
  const openObsidian=element("button",null,"Open in Obsidian");openObsidian.type="button";
  openObsidian.addEventListener("click",()=>post("/api/vault/action",{url:p.taskObsidianPath}).catch(()=>{}));
  const status=element("span","copy-status");
  const copyId=element("button",null,"Copy Task ID");copyId.type="button";
  copyId.addEventListener("click",()=>copyToClipboard(p.taskId,"Task ID",status));
  const copyLink=element("button",null,"Copy Task link");copyLink.type="button";
  copyLink.addEventListener("click",()=>copyToClipboard(p.taskDeepLink,"Task link",status));
  actions.append(refresh,openGlasswork,openObsidian,copyId,copyLink,status);
  header.append(actions);
  header.append(element("p","readonly-indicator","Read-only view — actions here don't change this Task. Edit in Glasswork or Obsidian."));
  section.append(header);
  if(p.isStale){
    const banner=element("div","stale-banner");
    banner.setAttribute("role","status");
    banner.append(element("span",null,"Showing the last known good data — "+(p.staleError||"couldn't refresh this Task.")));
    const retry=element("button",null,"Retry");retry.type="button";
    retry.addEventListener("click",()=>retryTask(p.taskId));
    banner.append(retry);
    section.append(banner);
  }
  const description=element("section","card");description.append(element("h2",null,"Description"));const descriptionBody=element("div","markdown");descriptionBody.innerHTML=p.descriptionHtml||"<p class='muted'>No description.</p>";description.append(descriptionBody);section.append(description);
  const notes=element("section","card");notes.append(element("h2",null,"Notes"));const notesBody=element("div","markdown");notesBody.innerHTML=p.notesHtml||"<p class='muted'>No notes.</p>";notes.append(notesBody);section.append(notes);
  if(p.activeSubtasks.length||p.completedSubtasks.length){
    const subtasks=element("section","card");subtasks.append(element("h2",null,"Subtasks"));
    const activeList=element("div","subtask-list");
    p.activeSubtasks.forEach(s=>activeList.append(renderSubtaskRow(s)));
    subtasks.append(activeList);
    if(p.showCompletedSubtasks&&p.completedSubtasks.length){
      const completedDetails=element("details");
      completedDetails.dataset.stateKey="completed-subtasks:"+p.taskId;
      completedDetails.append(element("summary",null,`Completed (${p.completedSubtasks.length})`));
      const completedList=element("div","subtask-list");
      p.completedSubtasks.forEach(s=>completedList.append(renderSubtaskRow(s)));
      completedDetails.append(completedList);
      subtasks.append(completedDetails);
    }
    section.append(subtasks);
  }
  if(p.links.length){
    const linksSection=element("section","card");linksSection.append(element("h2",null,"Links"));
    p.links.forEach(link=>{
      const row=element("div","link-row");
      const badge=element("span","link-badge",link.typeBadgeText);badge.style.background=link.typeBadgeColor;row.append(badge);
      if(link.resolvedUrl){const btn=element("button","inline-link",link.displayText);btn.dataset.externalUrl=link.resolvedUrl;row.append(btn)}
      else row.append(element("span","muted",link.displayText));
      linksSection.append(row);
    });
    section.append(linksSection);
  }
  if(p.showRelated){
    const relatedSection=element("section","card");relatedSection.append(element("h2",null,"Related"));
    p.relatedEntries.forEach(entry=>{
      const btn=element("button","inline-link related-row");btn.type="button";btn.dataset.vaultPath=entry.vaultPath;
      btn.append(element("span",null,entry.typeGlyph),element("span",null,entry.title));
      if(entry.isMissing)btn.append(element("span","chip unavailable","missing"));
      relatedSection.append(btn);
    });
    section.append(relatedSection);
  }
  if(p.showChildren){
    const childrenSection=element("section","card");childrenSection.append(element("h2",null,`Children (${p.directChildren.length})`));
    p.directChildren.forEach(child=>{
      const btn=element("button","inline-link child-row");btn.type="button";btn.dataset.taskId=child.id;btn.textContent=child.title||child.id;
      childrenSection.append(btn);
    });
    section.append(childrenSection);
  }
  if(p.showBacklinks){
    const backlinksSection=element("section","card");backlinksSection.append(element("h2",null,`Backlinks (${p.backlinks.length})`));
    p.backlinks.forEach(bl=>{
      const btn=element("button","inline-link backlink-row");btn.type="button";btn.dataset.vaultPath=bl.path;
      btn.append(element("span",null,bl.title),element("span","chip",bl.typeLabel));
      backlinksSection.append(btn);
    });
    section.append(backlinksSection);
  }
  const metadata=element("div","metadata");
  const created=isoDate(p.created);
  if(created)metadata.append(element("p",null,"Created: "+created));
  if(p.completedAt){const c=isoDateTime(p.completedAt);if(c)metadata.append(element("p",null,"Completed: "+c))}
  if(p.cancelledAt){const c=isoDateTime(p.cancelledAt);if(c)metadata.append(element("p",null,`Cancelled: ${c} - ${p.cancellationReason||""}`))}
  section.append(metadata);
  if(p.artifactRows.length){const heading=element("h2",null,"Artifacts");heading.style.marginTop="20px";section.append(heading);p.artifactRows.forEach(row=>section.append(renderArtifact(row,p.taskId)))}
  return section;
}
function renderDetail(){
  const detail=element("div","detail");
  detail.setAttribute("role","region");
  detail.setAttribute("aria-label","Task detail");
  if(!data.selectedTaskId){detail.append(renderEmptyState());return detail}
  const sd=data.selectedDetail;
  if(!sd||sd.kind==="error"){detail.append(renderErrorState(sd||{message:"This Task is unavailable."}));return detail}
  detail.append(renderTask({...sd.projection,isStale:sd.isStale,staleError:sd.staleError}));
  return detail;
}
function captureUiState(){
  const state={windowScrollY:window.scrollY,railScrollTop:0,openDetails:new Set(),seenKeys:new Set(),focusedRailTaskId:null};
  const railList=app.querySelector(".rail-list");
  if(railList)state.railScrollTop=railList.scrollTop;
  app.querySelectorAll("details[data-state-key]").forEach(d=>{state.seenKeys.add(d.dataset.stateKey);if(d.open)state.openDetails.add(d.dataset.stateKey)});
  // Every render() rebuilds the rail's buttons from scratch, which would
  // otherwise silently drop keyboard focus (and roving-focus navigation,
  // issue #563) to document.body on each 5s poll. Track which row was
  // focused by its stable Task ID so applyUiState can restore focus to the
  // equivalent freshly-rendered button.
  const focused=document.activeElement;
  if(focused&&focused.classList&&focused.classList.contains("rail-select"))state.focusedRailTaskId=focused.dataset.railTaskId||null;
  return state;
}
function applyUiState(state){
  if(!state)return;
  const railList=app.querySelector(".rail-list");
  if(railList)railList.scrollTop=state.railScrollTop||0;
  app.querySelectorAll("details[data-state-key]").forEach(d=>{
    // Only override the freshly-rendered default for a <details> that was
    // already present at capture time — otherwise a newly-appearing element
    // (e.g. an Artifact that wasn't loaded before, or the first render) would
    // always be forced closed instead of honoring its own server-computed
    // default (row.isExpanded).
    if(state.seenKeys.has(d.dataset.stateKey))d.open=state.openDetails.has(d.dataset.stateKey);
  });
  window.scrollTo(0,state.windowScrollY||0);
  if(state.focusedRailTaskId){
    const next=app.querySelector(".rail-select[data-rail-task-id=\""+CSS.escape(state.focusedRailTaskId)+"\"]");
    if(next)next.focus();
  }
}
function render(){
  const uiState=captureUiState();
  const row=element("div","body-row");
  row.append(renderRail(),renderDetail());
  const banners=[renderDriftBanner(),renderRestoreBanner()].filter(Boolean);
  app.replaceChildren(...banners,row);
  applyUiState(uiState);
}
async function refreshState(){
  data=await getJson("/canvas-state");
  render();
}
async function selectTask(taskId){
  await post("/api/tasks/select",{taskId});
  await refreshState();
}
async function unloadTask(taskId){
  await post("/api/tasks/unload",{taskId});
  await refreshState();
}
async function clearAll(){
  await post("/api/tasks/clear");
  await refreshState();
}
async function refreshSelected(){
  await post("/api/tasks/refresh-selected");
  await refreshState();
}
async function retryTask(taskId){
  await post("/api/tasks/refresh",{taskId});
  await refreshState();
}
app.addEventListener("click",event=>{
  const target=event.target.closest("[data-task-id],[data-external-url],[data-vault-path]");if(!target)return;
  if(target.dataset.taskId){post("/api/tasks/load",{taskIds:[target.dataset.taskId]}).then(refreshState).catch(()=>{});return}
  if(target.dataset.externalUrl)post("/api/link/action",{url:target.dataset.externalUrl}).catch(()=>{});
  if(target.dataset.vaultPath)post("/api/vault/action",{url:target.dataset.vaultPath}).catch(()=>{});
});
// Delegated (not per-row) so it keeps working across the 5s poll re-renders
// that replace every rail row: implements the ARIA listbox keyboard pattern
// (Up/Down/Home/End move focus among options) on top of the rows' already-
// native Tab/Enter/Space button operability.
app.addEventListener("keydown",event=>{
  if(!["ArrowDown","ArrowUp","Home","End"].includes(event.key))return;
  const list=event.target.closest(".rail-list");
  if(!list)return;
  const options=Array.from(list.querySelectorAll(".rail-select"));
  const current=options.indexOf(document.activeElement);
  if(current===-1)return;
  let next=current;
  if(event.key==="ArrowDown")next=Math.min(current+1,options.length-1);
  else if(event.key==="ArrowUp")next=Math.max(current-1,0);
  else if(event.key==="Home")next=0;
  else if(event.key==="End")next=options.length-1;
  event.preventDefault();
  options[next].focus();
});
render();
wideQuery.addEventListener("change",render);
// Background refresh only ever updates this already-open page's DOM — it
// never calls anything that opens or focuses the canvas, so it can never
// steal host focus the way an explicit load does.
pollHandle=setInterval(()=>{refreshState().catch(()=>{})},5000);
</script></body></html>
""";
}
