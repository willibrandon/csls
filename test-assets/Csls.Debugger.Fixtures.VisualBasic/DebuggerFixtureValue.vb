Imports System.Diagnostics

Namespace Global.Csls.Debugger.Fixtures.VisualBasic
    ''' <summary>
    ''' Provides a stable Visual Basic receiver for debugger function-evaluation tests.
    ''' </summary>
    <DebuggerDisplay("visual-basic={Me._number}", Type:="visual-basic-display")>
    Friend NotInheritable Class DebuggerFixtureValue
        ''' <summary>
        ''' Holds mutable static storage for debugger expression tests.
        ''' </summary>
        Friend Shared s_number As Integer

        Private ReadOnly _number As Integer

        ''' <summary>
        ''' Initializes the Visual Basic debugger receiver.
        ''' </summary>
        ''' <param name="number">The value returned by the debugger-visible method.</param>
        Friend Sub New(number As Integer)
            _number = number
        End Sub

        ''' <summary>
        ''' Gets the value returned by the debugger-visible method.
        ''' </summary>
        Friend ReadOnly Property Number As Integer
            Get
                Return _number
            End Get
        End Property

        ''' <summary>
        ''' Observes static storage through code compiled into the target process.
        ''' </summary>
        Friend Shared Function ReadStaticNumber() As Integer
            Return s_number
        End Function

        ''' <summary>
        ''' Initializes shared storage before the fixture stops for inspection.
        ''' </summary>
        ''' <param name="number">The input supplied to the fixture entry point.</param>
        Friend Shared Sub InitializeStaticNumber(number As Integer)
            s_number = number + 20
        End Sub

        ''' <summary>
        ''' Applies the target compiler's reference-null semantics to one value.
        ''' </summary>
        ''' <param name="value">The value tested by compiler-authored target code.</param>
        ''' <returns>True when the value is Nothing.</returns>
        Friend Shared Function CompilerIsNothing(value As Object) As Boolean
            Return value Is Nothing
        End Function

        ''' <summary>
        ''' Computes a stable result by executing target code.
        ''' </summary>
        ''' <returns>The stored number incremented by one.</returns>
        Friend Function NextNumber() As Integer
            Return Number + 1
        End Function

        ''' <summary>
        ''' Adds one debugger-supplied argument to the stored number.
        ''' </summary>
        ''' <param name="value">The value supplied by managed function evaluation.</param>
        ''' <returns>The stored number plus the supplied value.</returns>
        Friend Function AddNumber(value As Integer) As Integer
            Return Number + value
        End Function

        ''' <summary>
        ''' Combines named instance arguments with this receiver's stored number.
        ''' </summary>
        ''' <param name="first">The high-order value.</param>
        ''' <param name="second">The low-order value.</param>
        ''' <returns>The order-sensitive instance result.</returns>
        Friend Function CombineNamed(first As Integer, second As Integer) As Integer
            Return first * 100 + second + _number
        End Function

        ''' <summary>
        ''' Combines named static arguments in CLR parameter order.
        ''' </summary>
        ''' <param name="first">The high-order value.</param>
        ''' <param name="second">The low-order value.</param>
        ''' <returns>The order-sensitive static result.</returns>
        Friend Shared Function CombineNamedStatic(first As Integer, second As Integer) As Integer
            Return first * 100 + second
        End Function

        ''' <summary>
        ''' Exposes a Visual Basic optional parameter through loaded method metadata.
        ''' </summary>
        ''' <param name="first">The required high-order value.</param>
        ''' <param name="second">The optional low-order value.</param>
        ''' <returns>The order-sensitive result.</returns>
        Friend Shared Function CombineOptionalStatic(first As Integer, Optional second As Integer = 42) As Integer
            Return first * 100 + second
        End Function

        ''' <summary>
        ''' Exposes a scaled negative Visual Basic decimal default.
        ''' </summary>
        ''' <param name="amount">The optional decimal value.</param>
        ''' <returns>One when the target receives its exact decimal representation.</returns>
        Friend Shared Function OptionalDecimalStatic(Optional amount As Decimal = -12.50D) As Integer
            Dim bits = Decimal.GetBits(amount)
            Return If(bits(0) = 1250 AndAlso bits(1) = 0 AndAlso bits(2) = 0 AndAlso
                bits(3) = &H80020000, 1, 0)
        End Function

        ''' <summary>
        ''' Exposes a Visual Basic date default through method metadata.
        ''' </summary>
        ''' <param name="when">The optional date value.</param>
        ''' <returns>One when the target receives its declared date.</returns>
        Friend Shared Function OptionalDateStatic(Optional [when] As DateTime = #1/1/2020#) As Integer
            Return If([when].Ticks = 637134336000000000L AndAlso
                [when].Kind = DateTimeKind.Unspecified, 1, 0)
        End Function

        ''' <summary>
        ''' Returns the length of a debugger-materialized string.
        ''' </summary>
        ''' <param name="value">The string supplied by managed function evaluation.</param>
        ''' <returns>The supplied string length.</returns>
        Friend Function StringLength(value As String) As Integer
            Return value.Length + Number - 41
        End Function
    End Class
End Namespace
