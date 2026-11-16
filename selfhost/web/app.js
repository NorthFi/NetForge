// root/selfhost/web/app.js
'use strict';
const root=document.querySelector('#root'),identity=document.querySelector('#identity'),logout=document.querySelector('#logout');
let csrf='',role='',username='',tab='Dashboard',data={};
const views=['Dashboard','Customers','Services','Packages','Highsites','Sectors','Equipment','RADIUS NAS','RADIUS Sessions','RADIUS Usage','RADIUS Operations','Tickets','Billing','Billing Policy','Customer Portal','Staff'];
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
const money=n=>new Intl.NumberFormat('en-ZA',{style:'currency',currency:'ZAR'}).format(Number(n)||0);
async function api(path,method='GET',payload){
 const opt={method,credentials:'same-origin',headers:{Accept:'application/json'}};
 if(method!=='GET'&&csrf)opt.headers['X-CSRF-Token']=csrf;
 if(payload!==undefined){opt.headers['Content-Type']='application/json';opt.body=JSON.stringify(payload)}
 const response=await fetch('/api'+path,opt);
 let body;try{body=await response.json()}catch{throw Error('Server returned an invalid response')}
 if(!response.ok)throw Error(body.error||body.title||'Request failed ('+response.status+')');
 return body;
}
function login(){
 root.innerHTML='<main><div class="login card"><h1>Sign in to NetForge</h1><p class="sub">Administrator, dispatcher or technician access.</p><form id="signin"><label>Username</label><input name="username" required autocomplete="username"><label>Password</label><input name="password" type="password" required autocomplete="current-password"><p id="loginerror" class="error" role="alert"></p><button class="primary">Sign in</button></form></div></main>';
 logout.hidden=true;identity.textContent='';
 root.querySelector('#signin').onsubmit=async e=>{e.preventDefault();try{const response=await api('/login','POST',Object.fromEntries(new FormData(e.target)));csrf=response.csrf;role=response.role;username=response.username;await boot(true)}catch(err){root.querySelector('#loginerror').textContent=err.message}};
}
async function boot(skipAuth=false){
 try{
  if(!skipAuth){const session=await api('/status');csrf=session.csrf;role=session.role;username=session.username}
  identity.textContent=username+' · '+role;logout.hidden=false;
  root.innerHTML='<div class="shell"><nav id="nav"></nav><main id="content"></main></div>';
  views.forEach(v=>{if((v==='Staff'||v==='Customer Portal')&&role!=='admin')return;if(v==='Billing'&&!['admin','dispatcher'].includes(role))return;
   if(v==='Billing Policy'&&role!=='admin')return;
   if(['RADIUS Usage','RADIUS Operations'].includes(v)&&!['admin','dispatcher'].includes(role))return;if(v==='RADIUS NAS'&&!['admin','dispatcher'].includes(role))return;
   let b=document.createElement('button');b.textContent=v;b.onclick=()=>{tab=v;render()};b.dataset.tab=v;root.querySelector('#nav').append(b)});
  await refresh();
 }catch{login()}
}
async function refresh(){
 const names=['customers','services','packages','highsites','sectors','tickets','branches'];
 const endpoints={customers:'/customers',services:'/services',packages:'/packages',highsites:'/highsites',sectors:'/sectors',tickets:'/tickets',
  nas:'/radius/nas-management',sessions:'/radius/sessions',operations:'/radius/operations',invoices:'/invoices',staff:'/staff',equipment:'/equipment'};
 const need=tab==='RADIUS NAS'?['nas']:tab==='RADIUS Sessions'?['sessions']:tab==='Billing'?['invoices']:tab==='Staff'?['staff']:tab==='Equipment'?['equipment']:tab==='RADIUS Operations'?['operations']:[];
 const failed=[];
 await Promise.all([...names,...need].map(async n=>{try{data[n]=await api(endpoints[n])}catch(e){failed.push(n+': '+e.message);data[n]=[]}}));
 render();
 if(failed.length)notice('Unavailable datasets: '+failed.join('; '),true);
}
function render(){
 root.querySelectorAll('nav button').forEach(b=>b.className=b.dataset.tab===tab?'active':'');
 const content=root.querySelector('#content');content.innerHTML='<div class="sub">NETFORGE · '+esc(tab.toUpperCase())+'</div><p id="message" role="status"></p>';
 if(tab==='Dashboard')dashboard(content);
 else if(tab==='Customers')list(content,'customers','Customer accounts',['account','first_name','surname','phone','status','highsite','sector']);
 else if(tab==='Services')list(content,'services','Customer services & PPPoE',['account','username','package','state','suspension_reason','static_ipv4','radius_enabled']);
 else if(tab==='Packages')list(content,'packages','Service packages',['name','download_mbps','upload_mbps','monthly_price','mikrotik_rate_limit']);
 else if(tab==='Highsites')list(content,'highsites','Highsites',['number','name','branch','latitude','longitude','subscribers']);
 else if(tab==='Sectors')list(content,'sectors','Access sectors',['highsite','name','ssid','azimuth','frequency_mhz','max_customers','customers']);
 else if(tab==='Equipment')list(content,'equipment','Site & customer equipment',['name','model','category','account','highsite','management_ip','serial_number','ownership']);
 else if(tab==='RADIUS NAS')list(content,'nas','RADIUS NAS clients',['shortname','nasname','vendor','disconnect_port','disconnect_enabled','description']);
 else if(tab==='RADIUS Sessions')list(content,'sessions','RADIUS accounting sessions',['username','nas_ip','acctsessionid','assigned_ip','acctstarttime','acctupdatetime','recently_seen']);
 else if(tab==='RADIUS Usage')radiusUsage(content);
 else if(tab==='RADIUS Operations')list(content,'operations','RADIUS disconnect operations',['created_at','username','acctsessionid','result','actor','reason','response_note']);
 else if(tab==='Tickets')list(content,'tickets','Support tickets',['number','subject','account','type','priority','status','created_at']);
 else if(tab==='Billing')list(content,'invoices','Invoices',['number','account','total','paid','outstanding','status','due_date']);
 else if(tab==='Billing Policy')billingPolicy(content);
 else if(tab==='Customer Portal')portalAccounts(content);
 else if(tab==='Staff')list(content,'staff','Staff accounts',['username','role','enabled','created_at']);
}
function notice(text,isError=false){
 const target=root.querySelector('#message');if(target){target.className=isError?'error':'notice';target.textContent=text}else alert(text);
}


function portalAccounts(el){
 if(role!=='admin'){el.textContent='Administrator access required.';return}
 el.innerHTML+='<h1>Customer portal accounts</h1><p class="sub">Separate subscriber credentials, revocable sessions and a customer-facing portal. Passwords are never retrievable.</p><div class="card"><h2>Provision subscriber access</h2><form id="portal-create"><div class="formgrid"><div><label>Customer</label><select id="portal-customer" name="customerId" required><option value="">Select customer</option>'+(data.customers||[]).map(c=>'<option value="'+esc(c.id)+'">'+esc(c.account)+' — '+esc(c.first_name)+' '+esc(c.surname)+'</option>').join('')+'</select></div><div><label>Portal username</label><input name="username" id="portal-username" required minlength="3" maxlength="80"></div><div><label>Initial password (at least 14 characters)</label><input name="password" type="password" minlength="14" autocomplete="new-password" required></div></div><div class="actions"><button class="primary">Create account</button><a href="/portal/" target="_blank" rel="noopener noreferrer">Open customer portal</a></div><p id="portal-note" role="status"></p></form></div><div class="card topgap"><div class="toolbar"><h2>Provisioned accounts</h2><button id="portal-reload">Refresh</button></div><div id="portal-list">Loading…</div></div>';
 const customer=el.querySelector('#portal-customer'),username=el.querySelector('#portal-username'),message=el.querySelector('#portal-note'),list=el.querySelector('#portal-list'),form=el.querySelector('#portal-create');
 customer.onchange=()=>{const found=(data.customers||[]).find(c=>c.id===customer.value);username.value=found?.account||''};
 async function draw(){
  try{
   const records=await api('/customer-portal/accounts');
   list.innerHTML='<div class="tablewrap"><table><thead><tr><th>Customer</th><th>Portal login</th><th>Status</th><th>Updated</th><th>Actions</th></tr></thead><tbody>'+records.map(r=>'<tr><td>'+esc(r.account)+'</td><td>'+esc(r.username)+'</td><td>'+esc(r.enabled?'Enabled':'Disabled')+'</td><td>'+esc(r.updated_at)+'</td><td><button data-op="reset" data-id="'+esc(r.customer_id)+'">Reset</button> <button data-op="'+(r.enabled?'disable':'enable')+'" data-id="'+esc(r.customer_id)+'">'+(r.enabled?'Disable':'Enable')+'</button></td></tr>').join('')+'</tbody></table></div>'+(records.length?'':'<p>No portal accounts yet.</p>');
   list.querySelectorAll('[data-op]').forEach(btn=>btn.onclick=async()=>{
    const action=btn.dataset.op,id=btn.dataset.id,body={action};
    if(action==='reset'){
     const next=prompt('Enter a new unique password with at least 14 characters. Share it securely with the subscriber.');
     if(next===null)return;
     if(next.length<14){message.textContent='Password must have at least 14 characters.';return}
     body.password=next;
    }else if(!confirm((action==='disable'?'Disable':'Enable')+' portal access? All previous login sessions will be revoked.'))return;
    try{await api('/customer-portal/accounts/'+id,'PUT',body);message.textContent='Account updated and previous sessions revoked.';await draw()}
    catch(e){message.textContent=e.message}
   });
  }catch(e){list.textContent=e.message}
 }
 form.onsubmit=async e=>{e.preventDefault();
  try{const values=Object.fromEntries(new FormData(form));await api('/customer-portal/accounts','POST',values);form.reset();message.textContent='Subscriber portal account created. Share credentials through a verified private channel.';await draw()}
  catch(e){message.textContent=e.message}
 };
 el.querySelector('#portal-reload').onclick=draw;draw();
}
async function ticketConversation(ticket){
 const dialogBox=dialog('<div class="toolbar"><div><h2>Ticket #'+esc(ticket.number)+'</h2><p class="sub">'+esc(ticket.subject)+'</p></div><button id="close">Close</button></div><div class="note">Public replies appear in the customer portal. Internal notes are visible to staff only.</div><div id="staff-messages">Loading…</div><form id="compose"><label>Reply visibility</label><select name="visibility"><option value="internal">Internal staff note</option><option value="public">Public subscriber reply</option></select><label>Message</label><textarea name="body" rows="5" minlength="1" maxlength="4000" required></textarea><p class="error" id="compose-error"></p><button class="primary">Post message</button></form>');
 dialogBox.querySelector('#close').onclick=()=>dialogBox.close();
 const output=dialogBox.querySelector('#staff-messages');
 async function draw(){
  try{const messages=await api('/tickets/'+ticket.id+'/messages');
   output.innerHTML=messages.map(m=>'<div class="card" style="margin:10px 0"><small>'+esc(m.visibility)+' · '+esc(m.actor_type)+' · '+esc(m.author)+' · '+esc(m.created_at)+'</small><p style="white-space:pre-wrap;overflow-wrap:anywhere">'+esc(m.body)+'</p></div>').join('')||'<p class="muted">No messages yet.</p>';
  }catch(e){output.textContent=e.message}
 }
 dialogBox.querySelector('#compose').onsubmit=async e=>{e.preventDefault();const values=Object.fromEntries(new FormData(e.target));
  try{await api('/tickets/'+ticket.id+'/messages','POST',values);e.target.querySelector('textarea').value='';dialogBox.querySelector('#compose-error').textContent='Message posted.';await draw()}
  catch(err){dialogBox.querySelector('#compose-error').textContent=err.message}
 };
 draw();
}
function numberGb(bytes){return ((Number(bytes)||0)/1073741824).toFixed(2)+' GB'}
function radiusUsage(el){
 const today=new Date().toISOString().slice(0,7);
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Subscriber RADIUS usage</h1><p class="sub">Observed RADIUS counter deltas, apportioned to UTC calendar months.</p></div><label>Month <input id="usage-period" type="month" value="'+today+'" min="2020-01"></label></div><p class="notice warning">Observed usage is split proportionally across UTC months. Missing historical intervals and counter resets remain excluded. Do not use as billable usage.</p><div id="usage-content" class="card">Loading…</div>');
 const input=el.querySelector('#usage-period'),content=el.querySelector('#usage-content');
 async function load(){content.textContent='Loading accounting data…';
  try{
   const result=await api('/radius/usage-reconciled?period='+encodeURIComponent(input.value));
   const rows=result.records;
   content.innerHTML='<div class="tablewrap"><table><thead><tr><th>Account</th><th>PPPoE login</th><th>Observed intervals</th><th>Download</th><th>Upload</th></tr></thead><tbody>'+rows.map(row=>'<tr><td>'+esc(row.account)+'</td><td>'+esc(row.username)+'</td><td>'+esc(row.observed_intervals)+'</td><td>'+esc(numberGb(row.download_bytes))+'</td><td>'+esc(numberGb(row.upload_bytes))+'</td></tr>').join('')+'</tbody></table></div><p class="sub">Data quality: '+esc(result.anomalyCount)+' detected counter anomalies. '+esc(result.note)+'</p>';
  }catch(e){content.textContent=e.message}
 }
 input.addEventListener('change',load);load();
}
function billingPolicy(el){
 el.insertAdjacentHTML('beforeend','<h1>Subscriber billing enforcement</h1><p class="sub">Opt-in automation: overdue issued invoices can block new PPPoE logins. Existing sessions are NOT disconnected automatically.</p><div class="card"><form id="policy-form"><label><input id="policy-enabled" name="enabled" type="checkbox" style="width:auto"> Automatically suspend overdue subscriber services</label><label>Grace period after due date (days)<input id="policy-grace" type="number" min="0" max="120" value="7" required></label><label><input id="policy-restore" type="checkbox" style="width:auto"> Automatically reactivate overdue-suspended services after debts are settled</label><p id="policy-preview" class="notice">Loading policy…</p><div class="buttons"><button class="primary">Save policy</button><button type="button" id="policy-run">Run policy now</button></div><p id="policy-result" role="status"></p></form></div>');
 const form=el.querySelector('#policy-form'),enabled=el.querySelector('#policy-enabled'),grace=el.querySelector('#policy-grace'),restore=el.querySelector('#policy-restore'),preview=el.querySelector('#policy-preview'),result=el.querySelector('#policy-result');
 async function load(){
  try{const x=await api('/billing/enforcement');
   enabled.checked=x.settings.enabled;grace.value=x.settings.grace_days;restore.checked=x.settings.restore_when_paid;
   preview.textContent='Current preview: '+x.preview.to_suspend+' service(s) would be suspended; '+x.preview.eligible_to_restore+' eligible to restore. Changes apply to all active customers with overdue issued invoices.';
  }catch(e){preview.textContent=e.message}
 }
 form.onsubmit=async e=>{e.preventDefault();
  const newSettings={enabled:enabled.checked,graceDays:Number(grace.value),restoreWhenPaid:restore.checked};
  if(newSettings.enabled&&!confirm('Enable automated service suspension for overdue accounts? This changes live RADIUS authentication. Existing sessions will remain active until separately disconnected.'))return;
  try{await api('/billing/enforcement','PUT',newSettings);result.textContent='Policy saved. Enabled='+newSettings.enabled;await load()}
  catch(e){result.textContent=e.message}
 };
 el.querySelector('#policy-run').onclick=async()=>{
  if(!enabled.checked){result.textContent='Enable and save the policy before running it.';return}
  if(!confirm('Apply the configured overdue-billing enforcement policy now?'))return;
  try{const r=await api('/billing/enforcement/run','POST',{});result.textContent=r.note+' Suspended: '+r.suspended+'; restored: '+r.restored;await refresh()}
  catch(e){result.textContent=e.message}
 };
 load();
}
function packagePicker(currentId){
 return '<select id="service-new-package">'+(data.packages||[]).map(p=>'<option value="'+esc(p.id)+'" '+(p.id===currentId?'selected':'')+'>'+esc(p.name)+' — '+esc(p.download_mbps)+'/'+esc(p.upload_mbps)+' Mbps</option>').join('')+'</select>';
}
function mikrotikInstructions(nas){
 const server=prompt('Enter your NetForge RADIUS server private IPv4 address (reachable by the router):');
 if(server===null)return;
 const octets=server.split('.').map(x=>Number(x));
 if(octets.length!==4||octets.some((v,i)=>!/^\d{1,3}$/.test(server.split('.')[i])||v>255)){alert('Enter a valid IPv4 address');return}
 const secret=prompt('Enter the same new NAS shared secret used during enrollment. It will not be saved by the browser:');
 if(secret===null)return;
 if(secret.length<16){alert('NAS shared secret must be at least 16 characters');return}
 const escapeRos=s=>'"'+s.replace(/[\\$"]/g,ch=>'\\'+ch).replace(/[\r\n]/g,'')+'"';
 const commands=[
  '/radius add service=ppp address='+server+' secret='+escapeRos(secret)+' authentication-port=1812 accounting-port=1813',
  '/ppp aaa set use-radius=yes accounting=yes interim-update=5m',
  '/radius incoming set accept=yes port='+String(nas.disconnect_port||1700)
 ].join('\n');
 const d=dialog('<h2>MikroTik RADIUS setup</h2><p class="notice warning">Review for existing /radius entries before using. Duplicate entries can cause login failures. Restrict UDP 1812/1813 to the NAS and the configured Disconnect port to your RADIUS source IP.</p><textarea id="ros-script" rows="8" readonly></textarea><p class="sub">This script is generated locally and not sent to the NetForge backend.</p><button id="copy-script" class="primary">Copy RouterOS commands</button><button id="close">Close</button>');
 d.querySelector('#ros-script').value=commands;
 d.querySelector('#copy-script').onclick=()=>navigator.clipboard.writeText(commands).then(()=>notice('Commands copied. Verify the NAS settings before applying.')).catch(()=>notice('Select the script text and copy manually.',true));
 d.querySelector('#close').onclick=()=>d.close();
}
function dashboard(el){
 el.insertAdjacentHTML('beforeend','<h1>ISP Operations</h1><p class="sub">CRM, subscriber services, RADIUS and network infrastructure.</p><div id="stats" class="grid"></div><div class="card topgap"><h2>Operational context</h2><p>RADIUS authentication and accounting are supplied by FreeRADIUS. Adding a NAS requires a FreeRADIUS restart. Suspending a service stops new authentications but does not disconnect an already established session.</p><button id="refresh" class="primary">Refresh dashboard</button></div>');
 api('/dashboard').then(r=>{const stats=[['Customers',r.customers],['Highsites',r.highsites],['Sectors',r.sectors],['Active services',r.active_services],['Open tickets',r.open_tickets],['Unclosed RADIUS sessions',r.unclosed_radius_sessions],['Invoices',r.invoices],['NAS devices',r.nas_devices]];
  el.querySelector('#stats').innerHTML=stats.map(([name,count])=>'<div class="card"><small>'+esc(name)+'</small><div class="metric">'+count+'</div></div>').join('');
 }).catch(e=>notice(e.message,true));
 el.querySelector('#refresh').onclick=refresh;
}
const forms={
 customers:[['account','Account name'],['firstName','First name'],['surname','Surname'],['email','Email'],['phone','Phone'],['address','Address','textarea'],['status','Status','status'],['branchId','Branch','branches'],['highsiteId','Highsite','highsites'],['sectorId','Sector','sectors']],
 services:[['customerId','Customer','customers'],['packageId','Package','packages'],['username','PPPoE username'],['state','Initial state','serviceState'],['staticIpv4','Static IPv4 (optional)']],
 packages:[['name','Package name'],['downloadMbps','Download Mbps','number'],['uploadMbps','Upload Mbps','number'],['monthlyPrice','Monthly price (ZAR)','decimal'],['mikrotikRateLimit','MikroTik-Rate-Limit (optional)']],
 highsites:[['number','Highsite number'],['name','Site name'],['address','Address'],['branchId','Branch','branches'],['latitude','Latitude','decimal'],['longitude','Longitude','decimal']],
 equipment:[['customerId','Customer','customers'],['highsiteId','Highsite','highsites'],['category','Category','equipCategory'],['name','Internal equipment name'],['model','Device model'],['managementIp','Management IP'],['serialNumber','Serial number'],['ownership','Ownership']],
 sectors:[['highsiteId','Highsite','highsites'],['name','Sector name'],['ssid','SSID'],['azimuth','Azimuth','decimal'],['frequencyMhz','Frequency MHz','decimal'],['maxCustomers','Planned subscribers','number']],
 nas:[['address','NAS IPv4 address'],['name','NAS name'],['secret','RADIUS shared secret (16+ characters)','password'],['description','Description']],
 tickets:[['customerId','Customer','customers'],['highsiteId','Highsite','highsites'],['subject','Subject'],['description','Description','textarea'],['type','Service type','ticketType'],['priority','Priority','priority']],
 invoices:[['customerId','Customer','customers'],['number','Invoice number'],['total','Amount','decimal'],['dueDate','Due date','date']],
 staff:[['username','Username'],['password','Password (14+ characters)','password'],['role','Role','role']]
};
const choices={equipCategory:['cpe','router','other','distribution','backhaul','access','power'],status:['active','suspended','pending','cancelled'],serviceState:['pending','suspended','cancelled'],ticketType:['customer','highsite','voip','other'],priority:['normal','low','high','critical'],role:['viewer','technician','dispatcher','admin']};
function lookupTable(collection){
 const arr=data[collection]||[];
 if(collection==='customers')return arr.map(x=>({id:x.id,label:x.account+' · '+x.first_name+' '+x.surname}));
 if(collection==='highsites')return arr.map(x=>({id:x.id,label:x.number+' · '+x.name}));
 if(collection==='sectors')return arr.map(x=>({id:x.id,label:x.name,highsite_id:x.highsite_id}));
 return arr.map(x=>({id:x.id,label:x.name}));
}
function formField([key,label,type],values={}){
 const value=values[key]??'',id='i_'+key;
 if(type==='textarea')return '<div class="wide"><label for="'+id+'">'+esc(label)+'</label><textarea id="'+id+'" name="'+key+'" rows="3">'+esc(value)+'</textarea></div>';
 if(type in choices)return '<div><label for="'+id+'">'+esc(label)+'</label><select id="'+id+'" name="'+key+'">'+choices[type].map(v=>'<option value="'+esc(v)+'" '+(v===value?'selected':'')+'>'+esc(v)+'</option>').join('')+'</select></div>';
 if(['customers','highsites','sectors','packages','branches'].includes(type))return '<div><label for="'+id+'">'+esc(label)+'</label><select id="'+id+'" name="'+key+'"><option value="">Unassigned</option>'+lookupTable(type).map(x=>'<option value="'+esc(x.id)+'" '+(x.id===value?'selected':'')+'>'+esc(x.label)+'</option>').join('')+'</select></div>';
 return '<div><label for="'+id+'">'+esc(label)+'</label><input id="'+id+'" name="'+key+'" '+(type==='password'?'type="password" autocomplete="new-password" minlength="12"':type==='number'?'type="number" step="1"':type==='decimal'?'type="number" step="0.01"':type==='date'?'type="date"':'')+' value="'+esc(value)+'"></div>';
}
function dialog(html){
 const d=document.createElement('dialog');d.innerHTML=html;document.body.append(d);d.onclose=()=>d.remove();d.showModal();return d;
}
function create(collection,values={}){
 const fields=forms[collection];if(!fields)return;
 const d=dialog('<div class="toolbar"><h2>Create '+esc(collection)+'</h2><button id="close">Close</button></div><form id="entry"><div class="formgrid">'+fields.map(f=>formField(f,values)).join('')+'</div><p id="error" class="error"></p><button class="primary">Save record</button></form>');
 d.querySelector('#close').onclick=()=>d.close();
 if(collection==='customers'){
  const hs=d.querySelector('[name="highsiteId"]'),sec=d.querySelector('[name="sectorId"]');
  const update=()=>{const prior=sec.value;sec.innerHTML='<option value="">Unassigned</option>'+lookupTable('sectors').filter(s=>s.highsite_id===hs.value).map(x=>'<option value="'+esc(x.id)+'">'+esc(x.label)+'</option>').join('');if([...sec.options].some(o=>o.value===prior))sec.value=prior};
  hs.onchange=update;update();
 }
 d.querySelector('#entry').onsubmit=async e=>{
  e.preventDefault();const raw=Object.fromEntries(new FormData(e.target)),value={};
  for(const [key,,type] of fields){
   let v=(raw[key]||'').trim();
   if(type==='number')v=v===''?null:Number(v);
   else if(type==='decimal')v=v===''?null:Number(v);
   else if(['customers','highsites','sectors','packages','branches'].includes(type))v=v||null;
   else if(type==='date')v=v||null;
   value[key]=v;
  }
  try{
   await api('/'+(collection==='nas'?'radius/nas':collection),'POST',value);
   d.close();await refresh();if(collection==='nas')notice('NAS added. Restart FreeRADIUS so it can load the new SQL client.');
  }catch(err){d.querySelector('#error').textContent=err.message}
 };
}
function list(el,collection,title,fields){
 let rows=data[collection]||[],query='';
 const adminOnly=['staff','nas','packages','invoices'],mayCreate=role==='admin'||(role!=='viewer'&&!adminOnly.includes(collection));
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>'+esc(title)+'</h1><p class="sub">'+rows.length+' records</p></div><div class="buttons"><button id="refresh">Refresh</button>'+(collection==='invoices'&&role==='admin'?'<button id="monthly">Generate monthly drafts</button>':'')+(mayCreate?'<button class="primary" id="add">+ Add</button>':'')+'</div></div><input id="search" placeholder="Search '+esc(collection)+'..." style="max-width:360px;margin-bottom:14px" aria-label="Search records"><div id="table"></div>');
 const draw=()=>{
  const shown=rows.filter(x=>JSON.stringify(x).toLowerCase().includes(query));
  el.querySelector('#table').innerHTML='<div class="card tablewrap"><table><thead><tr>'+fields.map(k=>'<th>'+esc(k.replaceAll('_',' '))+'</th>').join('')+'</tr></thead><tbody>'+shown.map(x=>'<tr data-id="'+esc(x.id||x.radacctid)+'">'+fields.map(k=>'<td>'+esc(x[k]??'—')+'</td>').join('')+'</tr>').join('')+'</tbody></table>'+(shown.length?'':'<p>No matching records.</p>')+'</div>';
  el.querySelectorAll('#table tr[data-id]').forEach(tr=>tr.onclick=()=>details(collection,rows.find(x=>String(x.id||x.radacctid)===tr.dataset.id)));
 };
 el.querySelector('#search').oninput=e=>{query=e.target.value.toLowerCase();draw()};
 el.querySelector('#refresh').onclick=refresh;
 const add=el.querySelector('#add');if(add)add.onclick=()=>create(collection);
 const monthly=el.querySelector('#monthly');
 if(monthly)monthly.onclick=async()=>{
  const period=prompt('Billing month (YYYY-MM):',new Date().toISOString().slice(0,7));
  if(period===null)return;
  if(!/^20\d{2}-(0[1-9]|1[0-2])$/.test(period)){notice('Use YYYY-MM, e.g. 2026-11',true);return}
  if(!confirm('Generate draft invoices for active, priced services in '+period+'? No payments or messages will be sent.'))return;
  try{const response=await api('/billing/monthly-drafts','POST',{period:period+'-01'});await refresh();notice(response.created+' new draft invoice(s) created for '+period)}
  catch(err){notice(err.message,true)}
 };

 draw();
}
function details(collection,r){
 if(!r)return;
 let html='<div class="toolbar"><h2>'+esc(r.account||r.name||r.username||r.subject||r.number||'Record')+'</h2><button id="close">Close</button></div><div class="grid">'+Object.entries(r).filter(([k])=>!['password_hash','secret'].includes(k)).map(([k,v])=>'<div class="card"><small>'+esc(k.replaceAll('_',' '))+'</small><div>'+esc(v===null?'—':v)+'</div></div>').join('')+'</div><div class="buttons topgap" id="actions"></div><p id="actionerror" class="error"></p>';
 const d=dialog(html);d.querySelector('#close').onclick=()=>d.close();
 const actions=d.querySelector('#actions');
 const btn=(name,handler)=>{const b=document.createElement('button');b.textContent=name;b.onclick=handler;actions.append(b)};
 const run=async (path,body={})=>{try{await api(path,'POST',body);d.close();await refresh()}catch(e){d.querySelector('#actionerror').textContent=e.message}};
 if(collection==='nas'&&role==='admin'){
  btn('Disconnect settings',()=>{
   const d2=dialog('<h2>NAS disconnect settings</h2><form id="nas-config"><label>NAS type</label><select name="vendor"><option value="mikrotik">MikroTik</option><option value="generic">Generic</option></select><label>Disconnect UDP port</label><input name="port" type="number" min="1" max="65535" value="'+esc(r.disconnect_port||1700)+'"><label><input name="enabled" type="checkbox" style="width:auto"> Enable Disconnect-Request on this NAS</label><p class="error" id="config-error"></p><button class="primary">Save NAS settings</button><button type="button" id="close">Cancel</button></form>');
   d2.querySelector('[name="vendor"]').value=r.vendor||'mikrotik';
   d2.querySelector('[name="enabled"]').checked=Boolean(r.disconnect_enabled);
   d2.querySelector('#close').onclick=()=>d2.close();
   d2.querySelector('#nas-config').onsubmit=async e=>{e.preventDefault();if(!confirm('Apply these Disconnect settings? UDP access must be restricted to the RADIUS host.'))return;
    try{await api('/radius/nas/'+r.id+'/settings','PUT',{vendor:d2.querySelector('[name="vendor"]').value,disconnectPort:Number(d2.querySelector('[name="port"]').value),disconnectEnabled:d2.querySelector('[name="enabled"]').checked});d2.close();d.close();await refresh()}
    catch(err){d2.querySelector('#config-error').textContent=err.message}
   };
  });
  btn('Rotate NAS shared secret',()=>{
   const secret=prompt('New shared secret (16+ characters). RADIUS must be restarted and the NAS updated afterwards.');
   if(secret===null)return;
   if(!confirm('Rotate the NAS secret? Until RouterOS and FreeRADIUS agree, subscriber logins can fail.'))return;
   run('/radius/nas/'+r.id+'/secret',{secret,confirmRestartRequired:true});
  });
  btn('Generate RouterOS setup instructions',()=>mikrotikInstructions(r));
 }
 if(collection==='sessions'&&role==='admin'&&r.recently_seen&&r.acctstoptime==null){
  btn('Disconnect recorded session',async()=>{
   const reason=prompt('Reason for disconnecting '+r.username+' on '+r.nas_ip+':');
   if(reason===null)return;
   if(reason.trim().length<5){d.querySelector('#actionerror').textContent='Reason must be at least 5 characters';return}
   if(!confirm('Send RADIUS Disconnect-Request for exactly this recorded session? This may interrupt customer service.'))return;
   try{
    const reply=await api('/radius/sessions/'+r.radacctid+'/disconnect','POST',{
     requestId:crypto.randomUUID(),reason:reason.trim(),confirm:true
    });d.close();await refresh();notice('Disconnect '+reply.result+': '+reply.note)}
   catch(e){d.querySelector('#actionerror').textContent=e.message}
  });
 }
 if(collection==='services'&&role==='admin'){
  btn('Set / rotate RADIUS password',()=>{
   const password=prompt('Enter a NEW subscriber PPPoE password (12–128 characters). This is not stored in your browser.');
   if(password===null)return;if(password.length<12){alert('Password too short');return}
   run('/services/'+r.id+'/credentials',{password});
  });
  btn('Change subscriber package',()=>{
   const d2=dialog('<h2>Change service package</h2><p>Applies new speed to future RADIUS logins. Connected sessions retain their existing rate until reconnection.</p>'+packagePicker(r.package_id)+'<p id="plan-error" class="error"></p><button id="plan-save" class="primary">Change package</button><button id="plan-cancel">Cancel</button>');
   d2.querySelector('#plan-cancel').onclick=()=>d2.close();
   d2.querySelector('#plan-save').onclick=async()=>{
    if(!confirm('Change the package for '+r.username+'?'))return;
    try{const next=await api('/services/'+r.id+'/package','PUT',{packageId:d2.querySelector('#service-new-package').value});d2.close();d.close();await refresh();notice('Package updated: '+next.rateLimit+'. Reconnect session to apply immediately.')}
    catch(e){d2.querySelector('#plan-error').textContent=e.message}
   };
  });
  btn('Suspend PPPoE authentication',()=>{if(confirm('Disable new PPPoE authentications? Existing sessions require CoA/disconnect.'))run('/services/'+r.id+'/suspend')});
  if(r.state==='suspended')btn('Reactivate PPPoE authentication',()=>{
   if(confirm('Reactivate this subscriber? The customer must be active and a PPPoE password must be stored.'))run('/services/'+r.id+'/reactivate')
  });
 }
 if(collection==='customers'&&role==='admin')
  btn('Customer portal access',()=>{d.close();tab='Customer Portal';render();const selector=root.querySelector('#portal-customer');if(selector){selector.value=r.id;selector.dispatchEvent(new Event('change'))}});
 if(collection==='tickets'){
  btn('Ticket conversation',()=>{d.close();ticketConversation(r)});
 }
 if(collection==='tickets'&&role!=='viewer'){
  ['in_progress','pending','resolved','closed'].forEach(status=>btn('Mark '+status,async()=>{
   try{await api('/tickets/'+r.id+'/status','PUT',{status});d.close();await refresh()}catch(e){d.querySelector('#actionerror').textContent=e.message}
  }));
 }
 if(collection==='invoices'&&role==='admin'&&r.status==='draft')
  btn('Issue invoice',()=>{if(confirm('Issue invoice '+r.number+'?'))run('/invoices/'+r.id+'/issue')});
 if(collection==='invoices'&&role==='admin'&&r.status==='issued')
  btn('Record payment',()=>{d.close();payment(r)});
}
function payment(invoice){
 const d=dialog('<h2>Record payment · '+esc(invoice.number)+'</h2><form id="pay"><label>Amount (ZAR)</label><input name="amount" type="number" min="0.01" step="0.01" required><label>Reference</label><input name="reference" required><p id="err" class="error"></p><button class="primary">Save payment</button><button type="button" id="cancel">Cancel</button></form>');
 d.querySelector('#cancel').onclick=()=>d.close();
 d.querySelector('#pay').onsubmit=async e=>{e.preventDefault();const raw=Object.fromEntries(new FormData(e.target));
  try{await api('/payments','POST',{invoiceId:invoice.id,amount:Number(raw.amount),reference:raw.reference});d.close();await refresh()}catch(err){d.querySelector('#err').textContent=err.message}};
}
logout.onclick=async()=>{try{await api('/logout','POST');csrf='';await boot()}catch(err){notice(err.message,true)}};
boot();
