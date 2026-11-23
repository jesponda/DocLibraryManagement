/* SGD Allegion · comportamientos mínimos del front (sin librerías).
   1. Menú lateral plegable (recuerda la elección) y menú deslizable en pantallas pequeñas.
   2. Tema claro/oscuro de Bootstrap según el sistema (los colores propios los resuelve el CSS).
   3. Filtros dependientes: un <select data-depende="id-del-padre"> solo ofrece las <option data-padre="valor"> del padre elegido.
   Todo es mejora progresiva: sin JavaScript las páginas funcionan igual (los filtros dependientes muestran todas las opciones). */
(function () {
  var raiz = document.documentElement;

  // Se aplica de inmediato (antes de pintar) para que no parpadee.
  try { if (localStorage.getItem('sgd.menu') === 'oculto') { raiz.classList.add('sgd-oculto'); } } catch (e) { /* almacenamiento bloqueado */ }

  var oscuro = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  function tema() { raiz.setAttribute('data-bs-theme', oscuro && oscuro.matches ? 'dark' : 'light'); }
  tema();
  if (oscuro && oscuro.addEventListener) { oscuro.addEventListener('change', tema); }

  function movil() { return window.matchMedia('(max-width: 860px)').matches; }
  function lateral() { return document.getElementById('lateral'); }
  function recordar(oculto) { try { localStorage.setItem('sgd.menu', oculto ? 'oculto' : 'visible'); } catch (e) { /* ignorar */ } }

  document.addEventListener('click', function (e) {
    var boton = e.target.closest ? e.target.closest('[data-sgd]') : null;
    var aside = lateral();

    if (boton && boton.dataset.sgd === 'ocultar') {
      if (movil()) { if (aside) { aside.classList.remove('abierto'); } }
      else { raiz.classList.add('sgd-oculto'); recordar(true); }
      return;
    }

    if (boton && boton.dataset.sgd === 'mostrar') {
      if (movil()) { if (aside) { aside.classList.toggle('abierto'); } }
      else { raiz.classList.remove('sgd-oculto'); recordar(false); }
      return;
    }

    // En pantallas pequeñas, elegir una opción del menú lo cierra.
    if (movil() && aside && e.target.closest && e.target.closest('.sgd-nav')) { aside.classList.remove('abierto'); }
  });

  function dependiente(hijo) {
    var padre = document.getElementById(hijo.dataset.depende);
    if (!padre) { return; }
    var valor = padre.value;
    var vacio = valor === '';

    Array.prototype.forEach.call(hijo.options, function (o) {
      if (o.value === '') { return; }
      var oculta = vacio || o.dataset.padre !== valor;
      o.hidden = oculta;
      o.disabled = oculta;
    });

    var actual = hijo.options[hijo.selectedIndex];
    if (actual && actual.value !== '' && actual.disabled) { hijo.value = ''; }
    hijo.disabled = vacio;
  }

  function iniciar() {
    Array.prototype.forEach.call(document.querySelectorAll('select[data-depende]'), dependiente);
  }

  document.addEventListener('change', function (e) {
    if (!e.target.id) { return; }
    Array.prototype.forEach.call(document.querySelectorAll('select[data-depende="' + e.target.id + '"]'), function (hijo) {
      hijo.value = '';
      dependiente(hijo);
    });
  });

  document.addEventListener('DOMContentLoaded', function () {
    iniciar();
    // La navegación mejorada de Blazor cambia el contenido sin recargar la página.
    if (window.Blazor && window.Blazor.addEventListener) { window.Blazor.addEventListener('enhancedload', iniciar); }
  });
})();
