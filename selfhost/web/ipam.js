// root/selfhost/web/ipam.js
// Operator-owned static address pools. Display the recorded state, never claim
// the address is free on external equipment just because NetForge has no record.
'use strict';
async function ipamConsole(el){
 const editable=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>IPv4 address management</h1><p class="sub">Dedicated static subscriber pools · RADIUS provisioning</p></div><div class="buttons"><button id="ipam-refresh">Refresh</button>'+(editable?'<button id="ipam-create" class="primary">+ Pool</button>':'')+'</div></div><p class="note">Use only ranges you control and exclude them from RouterOS dynamic PPP, DHCP and other allocation systems. Unclosed RADIUS accounting blocks reuse. No router configuration is pushed.</p><div id="ipam-pools"></div><div id="ipam-detail"></div>');
 const poolArea=el.querySelector('#ipam-pools'),detail=el.querySelector('#ipam-detail');
 const failure=err=>{if(poolArea.isConnected)notice(err.message,true)};
 let selected=null,request=0;
 async function load(){
  const pools=await api('/ipam/pools');
  if(!poolArea.isConnected)return;
  poolArea.innerHTML='<div class="card tablewrap"><table><thead><tr><th>Pool</th><th>Subnet</th><th>Gateway</th><th>Usable hosts</th><th>Assigned</th><th>Reservations</th><th></th></tr></thead><tbody>'+pools.map(p=>'<tr><td>'+esc(p.name)+'</td><td>'+esc(p.subnet)+'</td><td>'+esc(p.gateway||'—')+'</td><td>'+esc(p.usable)+'</td><td>'+esc(p.assigned)+'</td><td>'+esc(p.reserved)+'</td><td><button data-pool="'+esc(p.id)+'">Inspect</button></td></tr>').join('')+'</tbody></table>'+(pools.length?'':'<p>No pools configured.</p>')+'</div>';
  poolArea.querySelectorAll('[data-pool]').forEach(b=>b.onclick=()=>{selected=pools.find(p=>p.id===b.dataset.pool);show().catch(failure)});
  if(selected){selected=pools.find(p=>p.id===selected.id)||null;if(selected)await show()}
 }
 function form(title,fields,onSave){
  const d=dialog('<h2>'+esc(title)+'</h2><form id="ipam-form">'+fields+'<p class="error" id="ipam-error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async ev=>{
   ev.preventDefault();const submit=ev.target.querySelector('button.primary');submit.disabled=true;
   try{await onSave(Object.fromEntries(new FormData(ev.target)));d.close();await load()}
   catch(err){d.querySelector('#ipam-error').textContent=err.message;submit.disabled=false}
  };
 }
 async function show(){
  const p=selected,seq=++request;
  detail.textContent='Loading addresses…';
  const rows=await api('/ipam/pools/'+p.id+'/addresses');
  if(!poolArea.isConnected||seq!==request)return;
  const counts={};rows.forEach(x=>counts[x.state]=(counts[x.state]||0)+1);
  detail.innerHTML='<div class="toolbar topgap"><div><h2>'+esc(p.name)+' · '+esc(p.subnet)+'</h2><p class="sub">'+Object.entries(counts).map(([k,v])=>esc(k.replaceAll('_',' '))+': '+v).join(' · ')+'</p></div>'+(editable?'<button id="ipam-auto" class="primary">Allocate first free</button>':'')+'</div><input id="ipam-search" placeholder="Filter address, account, state" aria-label="Filter addresses"><div class="buttons topgap"><button id="ipam-prev">Previous</button><span id="ipam-page"></span><button id="ipam-next">Next</button></div><div id="ipam-addresses"></div>';
  let page=0;
  const draw=()=>{
   const q=detail.querySelector('#ipam-search').value.toLowerCase();
   const filtered=rows.filter(x=>JSON.stringify(x).toLowerCase().includes(q));
   page=Math.max(0,Math.min(page,Math.ceil(filtered.length/100)-1));
   detail.querySelector('#ipam-page').textContent='Page '+(page+1)+' / '+Math.max(1,Math.ceil(filtered.length/100));
   detail.querySelector('#ipam-prev').disabled=page===0;
   detail.querySelector('#ipam-next').disabled=(page+1)*100>=filtered.length;
   const visible=filtered.slice(page*100,page*100+100);
   detail.querySelector('#ipam-addresses').innerHTML='<div class="card tablewrap"><table><thead><tr><th>Address</th><th>State</th><th>Subscriber</th><th>Reason</th><th>Accounting / RADIUS</th><th></th></tr></thead><tbody>'+visible.map(x=>'<tr><td>'+esc(x.address)+'</td><td>'+esc(x.state.replaceAll('_',' '))+'</td><td>'+esc(x.account||'')+' '+esc(x.username||'')+'</td><td>'+esc(x.reason||'')+'</td><td>'+esc(x.observed?'unclosed':'—')+' / '+esc(x.radius_configured?'configured':'—')+'</td><td>'+(editable?'<button data-address="'+esc(x.address)+'">Actions</button>':'')+'</td></tr>').join('')+'</tbody></table></div>';
   detail.querySelectorAll('[data-address]').forEach(b=>b.onclick=()=>actions(p,visible.find(x=>x.address===b.dataset.address)));
  };
  detail.querySelector('#ipam-search').oninput=()=>{page=0;draw()};
  detail.querySelector('#ipam-prev').onclick=()=>{page--;draw()};
  detail.querySelector('#ipam-next').onclick=()=>{page++;draw()};
  if(editable)detail.querySelector('#ipam-auto').onclick=()=>allocate(p,null).catch(failure);
  draw();
 }
 async function allocate(p,address){
  const services=(await api('/services')).filter(s=>s.state==='pending'&&!s.static_ipv4);
  if(!services.length)throw Error('No pending services without an address.');
  form('Allocate '+(address||'first free address'),'<label>Pending service</label><select name="serviceId" required>'+services.map(s=>'<option value="'+esc(s.id)+'">'+esc(s.account)+' · '+esc(s.username)+'</option>').join('')+'</select><p>Allocation is for a future RADIUS login. No active session will be changed.</p>',async fields=>{
   await api('/ipam/pools/'+p.id+'/allocate','POST',{serviceId:fields.serviceId,address,confirm:true});
  });
 }
 function actions(p,x){
  if(x.state==='free'){
   const d=dialog('<h2>'+esc(x.address)+'</h2><div class="buttons"><button id="allocate">Allocate to service</button><button id="reserve">Reserve</button><button id="close">Close</button></div>');
   d.querySelector('#close').onclick=()=>d.close();
   d.querySelector('#allocate').onclick=()=>{d.close();allocate(p,x.address).catch(failure)};
   d.querySelector('#reserve').onclick=()=>{d.close();form('Reserve '+x.address,'<label>Reason</label><input name="reason" required minlength="3" maxlength="240">',f=>api('/ipam/pools/'+p.id+'/reserve','POST',{address:x.address,reason:f.reason,confirm:true}))};
  }else if(x.state==='reserved'){
   if(confirm('Remove reservation for '+x.address+'? Other RADIUS holds still apply.'))api('/ipam/pools/'+p.id+'/unreserve','POST',{address:x.address,confirm:true}).then(load).catch(failure);
  }else if(x.state==='assigned'&&x.service_state==='pending'){
   if(confirm('Release '+x.address+' from this pending service? The server rejects any service with RADIUS history.'))api('/ipam/pools/'+p.id+'/release','POST',{serviceId:x.service_id,confirm:true}).then(load).catch(failure);
  }else if(x.state==='assigned'){
   // IPv4 is validated by the API; no arbitrary user text enters the command.
   const d=dialog('<h2>RouterOS verification</h2><p>Run on the subscriber NAS after authentication. Read-only commands; no SSH/API connection is opened.</p><textarea readonly rows="3">'+esc('/ppp active print detail where address='+x.address)+'</textarea><button id="close">Close</button>');
   d.querySelector('#close').onclick=()=>d.close();
  }else notice('Address cannot be allocated while in state '+x.state,true);
 }
 el.querySelector('#ipam-refresh').onclick=()=>load().catch(failure);
 if(editable)el.querySelector('#ipam-create').onclick=()=>form('Create static subscriber pool','<label>Name</label><input name="name" required maxlength="100"><label>Canonical IPv4 subnet (/20–/30)</label><input name="subnet" required placeholder="192.0.2.0/24"><label>Gateway (optional permanent reservation)</label><input name="gateway"><label>Description</label><input name="description" maxlength="240"><p>Confirm only if the entire range is reserved for NetForge static subscriber allocation and does not overlap another allocator.</p>',f=>api('/ipam/pools','POST',{...f,confirm:true}));
 try{await load()}catch(err){failure(err)}
}
